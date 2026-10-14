# InfoPanel.WaveLink

An InfoPanel plugin that connects to Elgato Wave Link (it must be installed and running) and exposes its
inventory and live meters as JSON text sensors. Plugin id `wavelink-plugin`, container `wavelink`.

| id | name | value |
|---|---|---|
| `status` | Status | `up` or `absent` |
| `app_version` | App Version | Wave Link version, empty when absent |
| `state` | State | JSON `{ status, appVersion, devices, channels, mixes, outputDevices }` (camelCase) |
| `meters` | Meters | JSON `{ "<key>": {"left","right","leftDb","rightDb"} }`, left/right 0..100, dB = dBFS or `null` for silence; `{}` when absent |

## Meters

Keys: `device:{outputDeviceId}` (pre output volume), `channel:{id}` (post channel fader), `input:{subId}` (hardware input, e.g. the Wave Mic; `input:{id}` when no subId), `mix:{mixId}`. Mix meters need an opt-in subscription, which the plugin sends for every mix after each mix read. Mix meters show Wave Link's display value: at mix level 0.5 the meter reads -12 dB while the real output drops 10.5 dB.

Meter scale (all four kinds, verified -58..0 dBFS within 0.03 dB, sample peak): `dBFS = 60 * p - 60` for the app's 0..1 value `p`; `p <= 0` is silence and gives `null`.

## dB fields in `state`

Fitted to Wave Link 3.4 measurements, +-0.2 dB on levels 0.1..1 (extrapolated below); level 0 gives `null` (-inf); values are rounded to 2 decimals. The raw 0..1 levels are unchanged.

| field | formula |
|---|---|
| `channels[].levelDb` (channel fader) | `-84 * (1 - level^0.245)` |
| `channels[].mixes[].levelDb` (channel to mix send) | `-171 * (1 - level^0.22)` |
| `mixes[].levelDb` (mix master, the real signal change) | `34.8 * log10(level)` |
| `devices[].inputs[].gain.valueDb` | Interpolated from the app's own `gain.lookUpTable` (Wave Mic: `value * 40`, 1 dB steps); `null` when the table is empty (a third-party interface's gain has no dB scale) |

`outputDevices` is `[{ id, name, deviceType, outputs: [{ id, name, isMuted, level, mixId }] }]` (from `getOutputDevices`); `level` is the raw 0..1 value, no dB (unmeasured). `devices` remains the input devices.

`meters` holds, per key, the peak since the previous update (peak-hold), rounded to 2 decimals; a key keeps its last value until the channel is removed or the link drops (the app only sends changed levels). `state` is only reassigned when the inventory changes, and is refreshed from the app's change notifications (10 s backstop poll). An explicit port can be set for testing in
`%LOCALAPPDATA%\InfoPanel\WaveLink\wavelink.json` as `{ "port": 1234 }`; otherwise the port is read from the
Wave Link app's own `ws-info.json`.

Build: `dotnet build -c Release`. Deploy `InfoPanel.WaveLink.dll` and `PluginInfo.ini` to
`C:\ProgramData\InfoPanel\plugins\InfoPanel.WaveLink\`.

## Releasing

1. Bump `<Version>` in `InfoPanel.WaveLink/InfoPanel.WaveLink.csproj` and `Version` in `InfoPanel.WaveLink/PluginInfo.ini`, and add a `CHANGELOG.md` entry (`## [X.Y.Z] - yyyy-MM-dd`).
2. Commit, then push a tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.

The *Release Build* workflow (`.github/workflows/release.yml`) builds the plugin, checks the tag matches `<Version>`, and publishes `InfoPanel.WaveLink-vX.Y.Z.zip` to the GitHub release with the changelog section as notes. It can also be run manually (workflow_dispatch) to build without releasing.
