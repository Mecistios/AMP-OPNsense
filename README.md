# OPNsense Port Sync

AMP plugin that keeps your OPNsense port forwards in sync with your game server instances. Add an instance, change a port or remove one, and the matching WAN port forward on your OPNsense firewall is created, updated or removed to match.

Author: Mecistios. Built for AMP 2.8 (.NET 8).

## What it does

- Reads each AMP instance and the ports it uses (from the instance's `App.Ports`). The ADS controller and suspended instances are skipped.
- Creates one port forward (Destination NAT rule) per port or contiguous range, with the right protocol (TCP, UDP, or both).
- Leaves RCON and admin ports closed. Those are never forwarded to the internet.
- Compares that against the forwards already on your firewall:
  - a port already forwarded to the same host is left as it is
  - a port forwarded to a different host is reported as a conflict and not touched
  - only the ports that are genuinely missing get a forward created
- Only manages its own rules (matched by a description prefix), so anything you set up by hand is never changed or removed.

## Syncing

The plugin reacts to changes as they happen. It watches AMP's instance config on disk, so creating an instance, changing a port or deleting an instance triggers a sync within a few seconds. Only the changed instance's rules are looked at, so the rest of your forwards stay untouched. After a change it reloads the OPNsense filter so the rule takes effect right away.

A timer runs in the background as a fallback (default every few minutes). It does a full pass over all instances to catch drift, or anything the live sync missed. Set the interval to 0 to turn it off and sync manually only. You can also run a sync at any time with the Sync Now button in the settings.

## Requirements

- AMP 2.8 or newer.
- OPNsense with the API enabled and an API key/secret pair. The port forward (Destination NAT) API is available from OPNsense 26.1 onward.
- The plugin reads AMP's `instances.json`, so it has to run on the machine that hosts the instances (the ADS/controller).

## Building

1. Copy `ModuleShared.dll` from your AMP installation into `lib/`.
2. Run `dotnet build -c Release`.

The output is `bin/Release/net8.0/OpnsensePortSync.dll`. `ModuleShared.dll` is only used to build against and is not shipped, because AMP already provides it at runtime.

## Installing

1. Put `OpnsensePortSync.dll` in the instance's plugin folder: `.../instances/<instance>/Plugins/OpnsensePortSync/OpnsensePortSync.dll`
2. Add the plugin to that instance's load list in `AMPConfig.conf` while the instance is stopped:

   ```
   AMP.LoadPlugins=["OpnsensePortSync"]
   ```
3. Start the instance. The log should show `Loaded OpnsensePortSync by Mecistios`.

## Licensing

There is no plugin code signing yet, so on a normal licence AMP will not load this unsigned plugin. To run it, activate an AMP Developer licence on the instance next to your normal runtime licence. The developer licence is a companion to your main licence and does not use up instance slots.

The instance (the ADS controller in particular) needs both licences active at the same time. Reactivate with the developer key first, then with your runtime key, and AMP keeps both:

```
ampinstmgr --ReactivateInstance <instance> <developer-key>
ampinstmgr --ReactivateInstance <instance> <runtime-key>
```

The log then shows both licences present and the plugin loads.

## Getting an API key

In OPNsense, go to System > Access > Users, open your user and add an API key. OPNsense downloads a file with a `key` and `secret`. Put both into the plugin settings.

## Configuration

Open the instance configuration and go to Instance Deployment, tab OPNsense Network Automation. Fill in:

| Setting | Meaning |
|---|---|
| Enable OPNsense Port Sync | Master switch. When off, nothing is sent to OPNsense. |
| OPNsense Host / IP | Hostname or IP of the firewall. |
| OPNsense Port | HTTPS port of the GUI/API (usually 443). |
| API Key | API key (stored encrypted). |
| API Secret | API secret (stored encrypted). |
| WAN Interface | Interface the forwards apply to (usually `wan`). |
| Forward Target LAN IP | The LAN IP that game ports should forward to. |
| Sync Interval (minutes) | How often the fallback timer runs. 0 turns it off. |
| Rule Name Prefix | Prefix for the rule descriptions this plugin owns (default `AMP:`). |
| AMP Data Path | Optional. Leave blank to auto detect `instances.json`. |

## Endpoints

- `TestOpnsenseConnection` checks the connection and reports the firmware version.
- `SyncNow(apply)` returns the reconcile plan. Pass `apply=true` to make changes.
- `RunSync` applies a sync and reports a short summary (used by the button).
- `ListOpnsensePortForwards` lists the current forwards on the firewall.
