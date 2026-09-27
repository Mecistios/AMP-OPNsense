using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModuleShared;

namespace OpnsensePortSync
{
    // Anything tagged [JSONMethod] here becomes an endpoint the panel can call.
    internal class WebMethods : WebMethodsBase
    {
        private readonly PluginMain _plugin;
        private readonly PluginSettings _settings;
        private readonly ILogger _log;

        public WebMethods(PluginMain plugin, PluginSettings settings, ILogger log)
        {
            _plugin = plugin;
            _settings = settings;
            _log = log;
        }

        // Shown when an AMP update outdated this build; the settings cannot load then, so
        // every other guard would misreport the problem as missing configuration.
        private const string MismatchMessage = "This plugin build does not match the installed AMP version. Update the plugin from https://github.com/Mecistios/AMP-OPNsense/releases";

        [JSONMethod("Test the connection to the OPNsense firewall.",
            "Object with success flag, message and the firmware version.")]
        public async Task<object> TestOpnsenseConnection()
        {
            if (_plugin.BuildMismatch)
                return new Dictionary<string, object> { ["success"] = false, ["message"] = MismatchMessage };
            var o = _settings.Opnsense;
            if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.ApiSecret))
                return new Dictionary<string, object> { ["success"] = false, ["message"] = "Set Host, API key and API secret first." };
            try
            {
                using var c = _plugin.CreateClient();
                var ver = await c.GetVersionAsync();
                _log.Info($"OPNsense test OK: version {ver}.");
                return new Dictionary<string, object> { ["success"] = true, ["message"] = $"Connected. OPNsense {ver}." };
            }
            catch (Exception ex)
            {
                _log.Error($"OPNsense test connection failed: {ex.Message}");
                return new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message };
            }
        }

        [JSONMethod("Reconcile AMP instance ports with OPNsense port forwards. Set apply=true to make changes; otherwise a dry-run plan is returned.",
            "Object with the sync plan (create/update/delete/conflicts).")]
        public async Task<object> SyncNow(bool apply = false)
        {
            if (_plugin.BuildMismatch)
                return new Dictionary<string, object> { ["success"] = false, ["message"] = MismatchMessage };
            var o = _settings.Opnsense;
            if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.TargetLanIp))
                return new Dictionary<string, object> { ["success"] = false, ["message"] = "Set Host, API key and Target LAN IP first." };
            try
            {
                var plan = await _plugin.RunReconcileAsync(apply);
                if (plan.Error != null)
                    return new Dictionary<string, object> { ["success"] = false, ["message"] = plan.Error };
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["applied"] = apply,
                    ["create"] = plan.ToCreate,
                    ["update"] = plan.ToUpdate,
                    ["delete"] = plan.ToDelete,
                    ["conflicts"] = plan.Conflicts,
                    ["alreadyForwarded"] = plan.AlreadyForwarded.Count,
                    ["unchanged"] = plan.Unchanged.Count
                };
            }
            catch (Exception ex)
            {
                _log.Error($"OPNsense sync failed: {ex.Message}");
                return new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message };
            }
        }

        // Backs the "Test Connection" button. Connects and reports the firmware version.
        [JSONMethod("Test the OPNsense connection.", "Success/failure with the firmware version.")]
        public async Task<ActionResult> TestConnection()
        {
            if (_plugin.BuildMismatch)
                return ActionResult.FailureReason((FormattableString)$"{MismatchMessage}", "", (FormattableString)null);
            _log.Info("OPNsense Test Connection requested.");
            var o = _settings.Opnsense;
            if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.ApiSecret))
            {
                _log.Warning("OPNsense Test Connection: Host, API key or API secret not set (save the settings first).");
                return ActionResult.FailureReason((FormattableString)$"Set Host, API key and API secret first, then save.", "", (FormattableString)null);
            }
            try
            {
                using var c = _plugin.CreateClient();
                var ver = await c.GetVersionAsync();
                _log.Info($"OPNsense Test Connection OK: version {ver}.");
                return new ActionResult(true, (FormattableString)$"Connected. OPNsense {ver}.", "", (FormattableString)null);
            }
            catch (Exception ex)
            {
                _log.Error($"OPNsense Test Connection failed: {ex.Message}");
                return ActionResult.FailureReason((FormattableString)$"Connection failed: {ex.Message}", "", (FormattableString)null);
            }
        }

        // Backs the "Sync Now" button in the settings tab. Applies and reports a summary.
        [JSONMethod("Run a sync now and apply any changes to OPNsense.", "Success/failure with a short summary.")]
        public async Task<ActionResult> RunSync()
        {
            if (_plugin.BuildMismatch)
                return ActionResult.FailureReason((FormattableString)$"{MismatchMessage}", "", (FormattableString)null);
            var o = _settings.Opnsense;
            if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.TargetLanIp))
                return ActionResult.FailureReason((FormattableString)$"Set Host, API key and Target LAN IP first.", "", (FormattableString)null);

            SyncPlan plan;
            try { plan = await _plugin.RunReconcileAsync(apply: true); }
            catch (Exception ex)
            {
                _log.Error($"OPNsense sync failed: {ex.Message}");
                return ActionResult.FailureReason((FormattableString)$"Sync failed: {ex.Message}", "", (FormattableString)null);
            }
            if (plan.Error != null)
                return ActionResult.FailureReason((FormattableString)$"Sync failed: {plan.Error}", "", (FormattableString)null);

            var summary = $"Sync complete. Created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count}, removed {plan.ToDelete.Count}, {plan.AlreadyForwarded.Count} already OK.";
            if (plan.Conflicts.Count > 0)
                summary += $" {plan.Conflicts.Count} conflict(s): " + string.Join("; ", plan.Conflicts);
            return new ActionResult(true, (FormattableString)$"{summary}", "", (FormattableString)null);
        }

        [JSONMethod("List the port-forward rules on the OPNsense firewall.", "Object with the current forwards.")]
        public async Task<object> ListOpnsensePortForwards()
        {
            if (_plugin.BuildMismatch)
                return new Dictionary<string, object> { ["success"] = false, ["message"] = MismatchMessage };
            try
            {
                using var c = _plugin.CreateClient();
                var fwds = await c.GetPortForwardsAsync();
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["count"] = fwds.Count,
                    ["forwards"] = fwds.Select(f => new Dictionary<string, object>
                    {
                        ["descr"] = f.Descr,
                        ["proto"] = f.Protocol,
                        ["dstPort"] = f.DestPort,
                        ["target"] = f.Target,
                        ["enabled"] = f.Enabled
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message };
            }
        }
    }
}
