using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpnsensePortSync
{
    // One destination-NAT (port forward) rule, limited to the fields we care about.
    internal class DNatRule
    {
        public string Uuid;
        public string Descr;
        public string Protocol = "tcp";   // tcp | udp | tcp/udp
        public string DestPort;           // "25565" or "20000-20010" or an alias name
        public string Target;             // redirect target IP
        public string LocalPort;          // redirect target port (start port for a range)
        public string SourceNet = "any";  // "any", an alias name or a network in CIDR form
        public bool Enabled = true;
        public bool IsAutomatic;          // rule OPNsense maintains itself (e.g. anti-lockout)
    }

    // Talks to the OPNsense firewall API (Destination NAT) with an API key and secret.
    internal class OpnsenseClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _wan;

        public OpnsenseClient(string host, int port, string apiKey, string apiSecret, string wanInterface)
        {
            _baseUrl = $"https://{host}:{port}";
            // Empty means all interfaces, which is the recommended default and keeps NAT loopback working.
            _wan = wanInterface ?? "";
            var handler = new HttpClientHandler
            {
                // OPNsense ships a self-signed cert by default.
                ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            var raw = Encoding.ASCII.GetBytes($"{apiKey}:{apiSecret}");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        // Returns the firmware version string. Doubles as a connection / credential check.
        public async Task<string> GetVersionAsync()
        {
            var resp = await _http.PostAsync($"{_baseUrl}/api/core/firmware/status", EmptyJson());
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("product", out var prod) &&
                prod.TryGetProperty("product_version", out var ver))
                return ver.GetString();
            return "unknown";
        }

        public async Task<List<DNatRule>> GetPortForwardsAsync()
        {
            var resp = await _http.PostAsync($"{_baseUrl}/api/firewall/d_nat/searchRule", EmptyJson());
            resp.EnsureSuccessStatusCode();
            var rules = new List<DNatRule>();
            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            {
                if (!doc.RootElement.TryGetProperty("rows", out var rows)) return rules;
                foreach (var r in rows.EnumerateArray())
                {
                    rules.Add(new DNatRule
                    {
                        Uuid = Str(r, "uuid"),
                        Descr = Str(r, "descr"),
                        Protocol = Str(r, "protocol"),
                        DestPort = Str(r, "destination.port"),
                        SourceNet = Str(r, "source.network") ?? "any",
                        Enabled = Str(r, "disabled") != "1",
                        IsAutomatic = Str(r, "is_automatic") == "1"
                    });
                }
            }
            // searchRule does not return target / local-port, so pull those per rule.
            // Only real (uuid-based) rules can be fetched; skip the automatic ones.
            foreach (var rule in rules)
            {
                if (rule.IsAutomatic || rule.Uuid == null || !rule.Uuid.Contains("-")) continue;
                try
                {
                    var g = await _http.GetAsync($"{_baseUrl}/api/firewall/d_nat/getRule/{rule.Uuid}");
                    if (!g.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await g.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("rule", out var rr))
                    {
                        rule.Target = Str(rr, "target");
                        rule.LocalPort = Str(rr, "local-port");
                    }
                }
                catch { /* leave target/local-port null for this rule */ }
            }
            return rules;
        }

        public async Task CreatePortForwardAsync(DNatRule rule)
        {
            var resp = await _http.PostAsync($"{_baseUrl}/api/firewall/d_nat/addRule", RuleBody(rule));
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            EnsureSaved(doc, "add");
        }

        public async Task UpdatePortForwardAsync(string uuid, DNatRule rule)
        {
            var resp = await _http.PostAsync($"{_baseUrl}/api/firewall/d_nat/setRule/{uuid}", RuleBody(rule));
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            EnsureSaved(doc, "set");
        }

        public async Task DeletePortForwardAsync(string uuid)
        {
            var resp = await _http.PostAsync($"{_baseUrl}/api/firewall/d_nat/delRule/{uuid}", EmptyJson());
            resp.EnsureSuccessStatusCode();
        }

        // Reload the packet filter so staged rule changes take effect.
        public async Task ApplyAsync()
        {
            var resp = await _http.PostAsync($"{_baseUrl}/api/firewall/d_nat/apply", EmptyJson());
            resp.EnsureSuccessStatusCode();
        }

        // Build the {"rule": {...}} body. source/destination are nested objects; OPNsense
        // ignores the flat "destination_port" style, so they have to go in nested.
        // Destination is "(self)" ("This Firewall"), so only traffic aimed at one of the
        // firewall's own addresses is forwarded, and NAT loopback keeps working.
        // Source is "any" unless the user limited it to an alias or network; OPNsense
        // validates the value itself and rejects an alias that does not exist.
        private StringContent RuleBody(DNatRule rule)
        {
            var body = new
            {
                rule = new Dictionary<string, object>
                {
                    ["disabled"] = rule.Enabled ? "0" : "1",
                    ["interface"] = _wan,
                    ["ipprotocol"] = "inet",
                    ["protocol"] = rule.Protocol,
                    ["source"] = new Dictionary<string, object> { ["network"] = string.IsNullOrWhiteSpace(rule.SourceNet) ? "any" : rule.SourceNet.Trim() },
                    ["destination"] = new Dictionary<string, object> { ["network"] = "(self)", ["port"] = rule.DestPort },
                    ["target"] = rule.Target,
                    ["local-port"] = rule.LocalPort ?? "",
                    ["descr"] = rule.Descr
                }
            };
            return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        private static void EnsureSaved(JsonDocument doc, string op)
        {
            var result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
            if (result == "saved" || result == "ok") return;
            var detail = doc.RootElement.TryGetProperty("validations", out var v) ? v.GetRawText() : doc.RootElement.GetRawText();
            throw new Exception($"OPNsense {op} rejected the rule: {detail}");
        }

        private static StringContent EmptyJson() => new StringContent("{}", Encoding.UTF8, "application/json");

        // Read a string field, tolerating the dotted keys the search endpoint returns
        // (e.g. "destination.port") and values that come back as numbers.
        private static string Str(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                _ => null
            };
        }

        public void Dispose() => _http?.Dispose();
    }
}
