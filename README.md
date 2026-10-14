# InfoPanel.WaveLink

An InfoPanel plugin that connects to Elgato Wave Link (it must be installed and running) and exposes its
inventory and live meters as JSON text sensors. Plugin id `wavelink-plugin`, container `wavelink`.

| id | name | value |
|---|---|---|
| `status` | Status | `up` or `absent` |
| `app_version` | App Version | Wave Link version, empty when absent |
| `state` | State | JSON `{ status, appVersion, devices, channels, mixes }` (camelCase) |
| `meters` | Meters | JSON `{ "device:{id}": {"left","right"}, "channel:{id}": {...} }`, 0..100; `{}` when absent |

`state` is only reassigned when the inventory changes. An explicit port can be set for testing in
`%LOCALAPPDATA%\InfoPanel\WaveLink\wavelink.json` as `{ "port": 1234 }`; otherwise the port is read from the
Wave Link app's own `ws-info.json`.

Build: `dotnet build -c Release`. Deploy `InfoPanel.WaveLink.dll` and `PluginInfo.ini` to
`C:\ProgramData\InfoPanel\plugins\InfoPanel.WaveLink\`.

## Releasing

1. Bump `<Version>` in `InfoPanel.WaveLink/InfoPanel.WaveLink.csproj` and `Version` in `InfoPanel.WaveLink/PluginInfo.ini`, and add a `CHANGELOG.md` entry (`## [X.Y.Z] - yyyy-MM-dd`).
2. Commit, then push a tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.

The *Release Build* workflow (`.github/workflows/release.yml`) builds the plugin, checks the tag matches `<Version>`, and publishes `InfoPanel.WaveLink-vX.Y.Z.zip` to the GitHub release with the changelog section as notes. It can also be run manually (workflow_dispatch) to build without releasing.
