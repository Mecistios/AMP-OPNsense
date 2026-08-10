using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpnsensePortSync
{
    // One port (or contiguous range) an instance needs open.
    internal class DesiredPort
    {
        public string Instance;
        public string Proto;    // tcp | udp | tcp_udp
        public int Port;
        public int Range = 1;
        public string DstPort => Range > 1 ? $"{Port}-{Port + Range - 1}" : Port.ToString();
    }

    // A single port-forward rule we want in OPNsense. One per port entry, since a rule's
    // port field holds a single port or a contiguous range (no comma lists).
    internal class DesiredRule
    {
        public string Instance;
        public string Proto;      // tcp | udp | tcp_udp
        public string PortSpec;   // "8110" or "7777-7810"
        public string LocalPort;  // redirect start port
        public string RuleName;   // rule description, also our ownership marker
    }

    internal class SyncPlan
    {
        public List<string> ToCreate = new List<string>();
        public List<string> ToUpdate = new List<string>();
        public List<string> ToDelete = new List<string>();
        public List<string> Conflicts = new List<string>();       // port used by a forward to a DIFFERENT host
        public List<string> AlreadyForwarded = new List<string>(); // already covered by an existing rule to our host
        public List<string> Unchanged = new List<string>();
        public string Error;

        public bool Changed => ToCreate.Count > 0 || ToUpdate.Count > 0 || ToDelete.Count > 0;
    }

    // Reads AMP instances + their ports from disk and reconciles OPNsense port forwards.
    internal class OpnsenseReconciler
    {
        private readonly OpnsenseConfig _cfg;

        public OpnsenseReconciler(OpnsenseConfig cfg) { _cfg = cfg; }

        // Locate the AMP data directory (contains instances.json) by walking up from known roots.
        private string FindAmpDataPath()
        {
            if (!string.IsNullOrWhiteSpace(_cfg.AmpDataPath) && File.Exists(Path.Combine(_cfg.AmpDataPath, "instances.json")))
                return _cfg.AmpDataPath;
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "instances.json")))
                        return dir.FullName;
            }
            return null;
        }

        // Every folder that holds instance subfolders: the default instances dir plus any
        // datastore locations named by the Path entries in instances.json. Instances can be
        // created on a separate datastore, so watching only the default dir would miss them.
        public List<string> ResolveInstanceDirs()
        {
            var dirs = new List<string>();
            var root = FindAmpDataPath();
            if (root == null) return dirs;

            void Add(string d)
            {
                if (d != null && Directory.Exists(d) && !dirs.Contains(d)) dirs.Add(d);
            }
            Add(Path.Combine(root, "instances"));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances.json")));
                foreach (var inst in CollectInstances(doc.RootElement))
                {
                    var dataDir = InstanceDataDir(inst);
                    if (dataDir != null) Add(Path.GetDirectoryName(dataDir.TrimEnd('/', '\\')));
                }
            }
            catch { /* unreadable json, stick with the default dir */ }
            return dirs;
        }

        // instances.json nests the instance objects under per-target entries; pull them all out.
        private static List<JsonElement> CollectInstances(JsonElement rootEl)
        {
            var found = new List<JsonElement>();
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("InstanceName", out _) && e.TryGetProperty("Module", out _)) found.Add(e);
                    foreach (var p in e.EnumerateObject()) Walk(p.Value);
                }
                else if (e.ValueKind == JsonValueKind.Array)
                    foreach (var i in e.EnumerateArray()) Walk(i);
            }
            Walk(rootEl);
            return found;
        }

        // The instance's own data directory as recorded in instances.json. Not always
        // <root>/instances/<name>: an instance made on a datastore lives wherever the
        // datastore points.
        private static string InstanceDataDir(JsonElement inst)
        {
            if (inst.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var val = p.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            return null;
        }

        private static string ProtoName(int p) => p switch { 0 => "tcp", 1 => "udp", 2 => "tcp_udp", _ => "tcp_udp" };

        // Ports that should never be forwarded to the internet (admin/RCON/echo/log).
        private static bool IsAdminPort(string portRef)
        {
            if (string.IsNullOrEmpty(portRef)) return false;
            var r = portRef.ToLowerInvariant();
            return r.Contains("rcon") || r.Contains("admin") || r.Contains("echo");
        }

        // Module names and kvp filenames do not always match: module "Minecraft" writes
        // MinecraftModule.kvp and "ADS" writes ADSModule.kvp, while "GenericModule" matches
        // as-is. Try the plain spelling first, then with the Module suffix.
        private static string ResolveKvpPath(string instDir, string module)
        {
            var kvp = Path.Combine(instDir, module + ".kvp");
            if (File.Exists(kvp)) return kvp;
            var alt = Path.Combine(instDir, module + "Module.kvp");
            return File.Exists(alt) ? alt : kvp;
        }

        // The dedicated Minecraft module has no App.Ports array; its game port is a single
        // Minecraft.PortNumber value. Turn that into one desired port entry. Forwarded as
        // tcp+udp so an enabled query port is covered too.
        private List<(string Ref, int Proto, int Port, int Range)> ParseMinecraftPorts(string kvpPath)
        {
            var result = new List<(string, int, int, int)>();
            if (!File.Exists(kvpPath)) return result;
            string line = File.ReadLines(kvpPath).FirstOrDefault(l => l.StartsWith("Minecraft.PortNumber="));
            if (line == null) return result;
            if (!int.TryParse(line.Substring("Minecraft.PortNumber=".Length).Trim(), out var port)) return result;
            if (port > 0) result.Add(("GamePort", 2, port, 1));
            return result;
        }

        // Parse the App.Ports=[...] JSON array from an instance's <Module>.kvp
        private List<(string Ref, int Proto, int Port, int Range)> ParsePorts(string kvpPath)
        {
            var result = new List<(string, int, int, int)>();
            if (!File.Exists(kvpPath)) return result;
            string line = File.ReadLines(kvpPath).FirstOrDefault(l => l.StartsWith("App.Ports="));
            if (line == null) return result;
            var json = line.Substring("App.Ports=".Length).Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    int proto = e.TryGetProperty("Protocol", out var pr) ? pr.GetInt32() : 2;
                    int port = e.TryGetProperty("Port", out var po) ? po.GetInt32() : 0;
                    int range = e.TryGetProperty("Range", out var ra) ? ra.GetInt32() : 1;
                    string rf = e.TryGetProperty("Ref", out var re) ? (re.GetString() ?? "Port") : "Port";
                    if (port > 0) result.Add((rf, proto, port, range < 1 ? 1 : range));
                }
            }
            catch { /* malformed line -> no ports */ }
            return result;
        }

        // Build the set of ports every (non-ADS, non-suspended) instance wants exposed.
        // When onlyInstance is set, restrict to that one instance (used for the live per-instance syncs).
        public List<DesiredPort> GetDesiredPorts(out string error, string onlyInstance = null)
        {
            error = null;
            var list = new List<DesiredPort>();
            var root = FindAmpDataPath();
            if (root == null) { error = "Could not locate AMP data path (instances.json). Set 'AMP data path' in settings."; return list; }

            var instancesJson = Path.Combine(root, "instances.json");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(instancesJson)); }
            catch (Exception ex) { error = "Failed to read instances.json: " + ex.Message; return list; }

            var instances = CollectInstances(doc.RootElement);

            foreach (var inst in instances)
            {
                string name = inst.GetProperty("InstanceName").GetString();
                string module = inst.GetProperty("Module").GetString();
                if (onlyInstance != null && !string.Equals(name, onlyInstance, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(module, "ADS", StringComparison.OrdinalIgnoreCase)) continue; // skip controller
                if (inst.TryGetProperty("Suspended", out var s) && s.ValueKind == JsonValueKind.True) continue;

                // Prefer the instance's own Path from instances.json; it differs from the
                // default layout when the instance sits on a datastore (issue #1).
                var instDir = InstanceDataDir(inst) ?? Path.Combine(root, "instances", name);
                var kvp = ResolveKvpPath(instDir, module);
                var ports = string.Equals(module, "Minecraft", StringComparison.OrdinalIgnoreCase)
                    ? ParseMinecraftPorts(kvp)
                    : ParsePorts(kvp);
                foreach (var (rf, proto, port, range) in ports)
                {
                    if (IsAdminPort(rf)) continue; // never expose RCON/admin ports
                    list.Add(new DesiredPort { Instance = name, Proto = ProtoName(proto), Port = port, Range = range });
                }
            }
            return list;
        }

        // Compute what would change in OPNsense to match the desired ports. Optionally apply.
        // When onlyInstance is set, only that instance's rules are touched, so one instance
        // change can't disturb the rest.
        public async Task<SyncPlan> ReconcileAsync(bool apply, string onlyInstance = null)
        {
            var plan = new SyncPlan();
            var desired = GetDesiredPorts(out var err, onlyInstance);
            if (err != null) { plan.Error = err; return plan; }

            using var client = new OpnsenseClient(_cfg.Host, _cfg.Port, _cfg.ApiKey, _cfg.ApiSecret, _cfg.WanInterface);
            List<DNatRule> existing;
            try { existing = await client.GetPortForwardsAsync(); }
            catch (Exception ex) { plan.Error = "OPNsense query failed: " + ex.Message; return plan; }

            string prefix = _cfg.RuleNamePrefix ?? "";
            var ours = existing.Where(f => !string.IsNullOrEmpty(prefix) && (f.Descr ?? "").StartsWith(prefix)).ToList();
            // Foreign = rules we do not own. Automatic rules (anti-lockout) are left out of
            // conflict checks; they sit on the LAN and never cover a WAN game port anyway.
            var foreign = existing.Where(f => !f.IsAutomatic && (string.IsNullOrEmpty(prefix) || !(f.Descr ?? "").StartsWith(prefix))).ToList();

            var instScope = onlyInstance == null ? null : $"{prefix}{onlyInstance} ";

            // One rule per port entry (a rule's port field is a single port or a range).
            var rules = desired.Select(p => new DesiredRule
            {
                Instance = p.Instance,
                Proto = p.Proto,
                PortSpec = p.DstPort,
                LocalPort = p.Port.ToString(),
                RuleName = $"{prefix}{p.Instance} {ProtoLabel(p.Proto)} {p.DstPort}"
            }).ToList();

            foreach (var d in rules)
            {
                var wanted = ExpandPorts(d.PortSpec);
                var coveredSameHost = new HashSet<int>();
                var conflictPorts = new HashSet<int>();

                foreach (var f in foreign.Where(f => ProtoOverlap(f.Protocol, d.Proto)))
                {
                    var fp = ExpandPorts(f.DestPort);
                    foreach (var p in wanted)
                    {
                        if (!fp.Contains(p)) continue;
                        if (string.Equals(f.Target, _cfg.TargetLanIp, StringComparison.OrdinalIgnoreCase))
                            coveredSameHost.Add(p);
                        else if (conflictPorts.Add(p))
                            plan.Conflicts.Add($"Port {p}/{d.Proto} for instance '{d.Instance}' is already forwarded to another host ({f.Target}) by rule '{f.Descr}', so it was skipped to avoid overwriting it.");
                    }
                }

                var mine = ours.FirstOrDefault(f => f.Descr == d.RuleName);

                // Any conflicting port means we leave this rule alone entirely.
                if (conflictPorts.Count > 0) continue;

                // Every wanted port is already forwarded to us by something else -> our rule is redundant.
                if (wanted.Count > 0 && coveredSameHost.Count == wanted.Count)
                {
                    plan.AlreadyForwarded.Add($"{d.RuleName}, covered by an existing rule");
                    if (mine != null)
                    {
                        plan.ToDelete.Add(mine.Descr);
                        if (apply && !string.IsNullOrEmpty(mine.Uuid)) await client.DeletePortForwardAsync(mine.Uuid);
                    }
                    continue;
                }

                if (mine == null)
                {
                    plan.ToCreate.Add($"{d.RuleName} -> {_cfg.TargetLanIp}:{d.PortSpec}/{d.Proto}");
                    if (apply) await client.CreatePortForwardAsync(ToRule(d));
                }
                else if (!string.Equals(mine.Protocol, ProtoApi(d.Proto), StringComparison.OrdinalIgnoreCase)
                         || mine.DestPort != d.PortSpec || mine.LocalPort != d.LocalPort
                         || !string.Equals(mine.Target, _cfg.TargetLanIp, StringComparison.OrdinalIgnoreCase)
                         || !mine.Enabled)
                {
                    plan.ToUpdate.Add($"{d.RuleName} -> {_cfg.TargetLanIp}:{d.PortSpec}/{d.Proto}");
                    if (apply) await client.UpdatePortForwardAsync(mine.Uuid, ToRule(d));
                }
                else plan.Unchanged.Add(d.RuleName);
            }

            // Orphans: our rules with no matching desired rule -> remove. On a targeted sync,
            // only look at this instance's rules so other instances are not affected.
            var wantedNames = rules.Select(d => d.RuleName).ToHashSet();
            var orphanScope = instScope == null ? ours : ours.Where(f => (f.Descr ?? "").StartsWith(instScope));
            foreach (var f in orphanScope.Where(f => !wantedNames.Contains(f.Descr)))
            {
                plan.ToDelete.Add(f.Descr);
                if (apply && !string.IsNullOrEmpty(f.Uuid)) await client.DeletePortForwardAsync(f.Uuid);
            }

            // Staged rule changes only take effect after a filter reload.
            if (apply && plan.Changed) await client.ApplyAsync();
            return plan;
        }

        private static string ProtoLabel(string p) => p switch { "udp" => "UDP", "tcp" => "TCP", "tcp_udp" => "TCP/UDP", _ => p.ToUpperInvariant() };

        // AMP protocol -> the value the OPNsense rule expects.
        private static string ProtoApi(string p) => p switch { "tcp" => "tcp", "udp" => "udp", "tcp_udp" => "tcp/udp", _ => "tcp/udp" };

        private DNatRule ToRule(DesiredRule d) => new DNatRule
        {
            Descr = d.RuleName,
            Enabled = true,
            Protocol = ProtoApi(d.Proto),
            DestPort = d.PortSpec,
            Target = _cfg.TargetLanIp,
            LocalPort = d.LocalPort
        };

        private static bool ProtoOverlap(string a, string b)
        {
            a = Norm(a); b = Norm(b);
            if (a == b) return true;
            return a == "tcp_udp" || b == "tcp_udp";
        }

        private static string Norm(string p) => (p ?? "").Replace("/", "_").ToLowerInvariant();

        // Expand a port spec ("8110", "7777-7810") into a set of ints. Non-numeric specs
        // (an alias name on a foreign rule) expand to nothing, so they never match a port.
        private static HashSet<int> ExpandPorts(string spec)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(spec)) return set;
            foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim();
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    if (int.TryParse(p.Substring(0, dash), out var lo) && int.TryParse(p.Substring(dash + 1), out var hi))
                        for (int i = lo; i <= hi && (i - lo) < 65536; i++) set.Add(i);
                }
                else if (int.TryParse(p, out var v)) set.Add(v);
            }
            return set;
        }
    }
}
