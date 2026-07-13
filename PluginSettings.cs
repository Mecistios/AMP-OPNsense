using System.ComponentModel;
using ModuleShared;

namespace OpnsensePortSync
{
    // Persisted plugin configuration. [Description(...)] is the Configuration menu item;
    // the WebSetting subcategory (below) is the tab shown within it.
    internal class PluginSettings : SettingStore
    {
        [Description("Instance Deployment")]
        public class OpnsenseSettings : SettingSectionStore
        {
            // Master on/off. When false the plugin does nothing (no calls to OPNsense).
            [WebSetting("Enable OPNsense Port Sync",
                "When enabled, AMP will keep OPNsense port forwards in sync with instance ports (create/update/remove).",
                false, "", "", "opnsense,ports,sync", false, "", "", "", "OPNsense Network Automation", 10)]
            [InlineAction("OpnsensePortSync", "RunSync", "Sync Now", "", false)]
            public bool Enabled = false;

            [WebSetting("OPNsense Host / IP",
                "Hostname or IP of the OPNsense firewall (e.g. 192.168.1.1 or firewall.example.com). Test Connection only shows a message when it fails; no response usually means the connection is OK.",
                false, "", "", "opnsense,host", false, "192.168.1.1", "", "", "OPNsense Network Automation", 20)]
            [InlineAction("OpnsensePortSync", "TestConnection", "Test Connection", "", false)]
            public string Host = "";

            [WebSetting("OPNsense Port",
                "HTTPS port of the OPNsense web GUI / API (usually 443).",
                false, "", "", "opnsense,port", false, "443", "", "", "OPNsense Network Automation", 30)]
            public int Port = 443;

            [WebSetting("API Key",
                "API key from OPNsense: System > Access > Users > (your user) > API keys.",
                false, "", "Password", "opnsense,apikey", false, "", "", "", "OPNsense Network Automation", 40)]
            [StoreEncrypted(true)]
            public string ApiKey = "";

            [WebSetting("API Secret",
                "API secret that came with the API key. Stored encrypted.",
                false, "", "Password", "opnsense,apisecret", false, "", "", "", "OPNsense Network Automation", 50)]
            [StoreEncrypted(true)]
            public string ApiSecret = "";

            [WebSetting("WAN Interface",
                "OPNsense interface the forwards apply to. Usually 'wan'.",
                false, "", "", "opnsense,wan,interface", false, "wan", "", "", "OPNsense Network Automation", 60)]
            public string WanInterface = "wan";

            [WebSetting("Forward Target LAN IP",
                "The LAN IP of this machine that game ports should be forwarded to (where AMP instances listen).",
                false, "", "", "opnsense,target,ip", false, "192.168.1.50", "", "", "OPNsense Network Automation", 70)]
            public string TargetLanIp = "";

            [WebSetting("Sync Interval (minutes)",
                "How often to automatically reconcile ports. Set to 0 to only sync manually.",
                false, "", "", "opnsense,interval", false, "15", "min", "", "OPNsense Network Automation", 80)]
            public int SyncIntervalMinutes = 15;

            [WebSetting("Rule Name Prefix",
                "Prefix for the port-forward descriptions this plugin creates, so it only manages its own rules.",
                false, "", "", "opnsense,prefix", false, "AMP:", "", "", "OPNsense Network Automation", 90)]
            public string RuleNamePrefix = "AMP:";

            [WebSetting("AMP Data Path (optional)",
                "Path to the AMP data folder that contains instances.json. Leave blank to auto-detect.",
                false, "", "", "opnsense,path", false, "", "", "", "OPNsense Network Automation", 100)]
            public string AmpDataPath = "";
        }

        public OpnsenseSettings Opnsense = new OpnsenseSettings();
    }
}
