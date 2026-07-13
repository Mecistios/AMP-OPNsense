/// <reference path="..\..\GSMyAdmin\WebRoot\Scripts\UI.js" />

this.plugin = {
    PushedMessage: function (source, message, parameters) {
        switch (message) {
            case "portconflict":
                UI.ShowModalAsync("OPNsense port conflict", parameters, UI.Icons.Exclamation, UI.OKActionOnly, "OPNsense Port Sync", parameters);
                break;
        }
    }
};
