# Changelog

## [1.2.0] - 2026-10-14

### Added
- `meters`: every entry gains `leftDb` / `rightDb` (dBFS, `null` for silence). New meter keys `input:{subId}` (hardware inputs such as the Wave Mic) and `mix:{id}` (the plugin subscribes to every mix's meter).
- `state`: `levelDb` on channels, channel mix routes and mixes; `valueDb` on input gain (from the app's own lookup table; `null` for gains without a dB scale). Formulas are fitted to Wave Link 3.4 measurements (see README).
- `state.outputDevices` from `getOutputDevices`, refreshed on the output device notifications.

### Changed
- Input, mix and output-device meter slots are pruned only when the matching inventory list explicitly drops their id.

## [1.1.0] - 2026-10-14

### Changed
- `meters` now publishes the peak since the previous update per key (peak-hold) instead of the latest sample, so peaks between the 20 Hz app ticks and the 10 Hz publish are no longer lost. A key with no new tick republishes its last value. Values are rounded to 2 decimals; the JSON shape is unchanged.
- Meter ticks are parsed straight from the socket bytes into preallocated slots (no per-message strings, documents or dictionaries); the plugin only reassigns `meters` when the text changed.
- The inventory (`state`) is event-driven: the app's `*Changed` notifications trigger a debounced (50 ms) re-read of the affected lists, so fader and mute changes show up in about 100 ms instead of up to 1 s. Input devices are now refreshed too. A 10 s backstop poll covers a missed notification.
- Connection and failure transitions are logged with `Console.WriteLine` (visible in the plugin host log) instead of `Debug.WriteLine`.

### Fixed
- A hung Wave Link app (socket open, no answers) now ends the attempt and reconnects: 3 consecutive stalled operations (a refresh pass or ping counts once), or 10 s of silence followed by an unanswered ping. WebSocket keep-alive is set to 20 s.
- RPC timeouts no longer leak a timer per call, and sends/waits honour the attempt's cancellation.
- Meter keys for removed channels no longer linger: a channel's meter is dropped when the inventory explicitly removes it. Meters never expire by time, since the app only sends changed levels.
- Messages over 4 MB end the attempt instead of growing without bound.
- Closing the plugin cannot race an in-flight update into a disposed collector.
