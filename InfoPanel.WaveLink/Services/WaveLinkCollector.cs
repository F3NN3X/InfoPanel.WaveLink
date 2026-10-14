using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.WaveLink.Services;

/* ------------------------------------------------------------------ *
 *  Normalized inventory records — the wire shapes the REST/SSE
 *  surface exposes (camelCase). Mirrors the Wave Link app's JSON-RPC
 *  responses, stripped to the fields the dashboard renders.
 * ------------------------------------------------------------------ */

/// <summary>A hardware input's fader position, with its dB range.</summary>
public sealed record WlGain(double? Value, double? Min, double? Max, double? ValueDb);

public sealed record WlInput(string Id, string Name, bool IsMuted, WlGain? Gain);

public sealed record WlDevice(string Id, string Name, string DeviceType, List<WlInput> Inputs);

/// <summary>A channel's route into an output mix, with the mix's level/mute.</summary>
public sealed record WlMixRoute(string Id, double? Level, bool IsMuted, double? LevelDb);

public sealed record WlApp(string Id, string Name);

public sealed record WlChannel(
    string Id,
    string Name,
    string Type,
    double? Level,
    bool IsMuted,
    List<WlMixRoute> Mixes,
    List<WlApp> Apps,
    double? LevelDb);

/// <summary>One of the app's output mixers (Mic Mix, Stream Mix, ...).</summary>
public sealed record WlMix(string Id, string Name, double? Level, bool IsMuted, double? LevelDb);

/// <summary>One output of an output device, routed to a mix; level is the raw 0..1 value (no dB: unmeasured).</summary>
public sealed record WlOutput(string Id, string Name, bool IsMuted, double? Level, string MixId);

public sealed record WlOutputDevice(string Id, string Name, string DeviceType, List<WlOutput> Outputs);

/// <summary>
/// The collector's current inventory. Replaced atomically (the reference is
/// swapped, never mutated), so a reader always sees a consistent snapshot.
/// </summary>
public sealed class WaveLinkState
{
    /// <summary>"up" (link established) or "absent" (app not reachable).</summary>
    public string Status { get; internal set; } = "absent";
    public string AppVersion { get; internal set; } = "";
    public List<WlDevice> Devices { get; internal set; } = new();
    public List<WlChannel> Channels { get; internal set; } = new();
    public List<WlMix> Mixes { get; internal set; } = new();
    public List<WlOutputDevice> OutputDevices { get; internal set; } = new();
}

/* ------------------------------------------------------------------ *
 *  The collector
 * ------------------------------------------------------------------ */

/// <summary>
/// The Wave Link app, over its local WebSocket — ported from the proven
/// f3nn3x-control-room client (modules/wavelink/server/wavelink.js).
///
/// The app is an MSIX package that opens one local port per launch and
/// rewrites its own port file on conflict, so the client never persists a
/// port: every attempt re-reads
/// %LOCALAPPDATA%\Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState\ws-info.json,
/// and no file is the absent state. An explicit port lives in the plugin's
/// own config (%LOCALAPPDATA%\InfoPanel\WaveLink\wavelink.json) as the test
/// seam.
///
/// The handshake is the oddity: the upgrade requires the
/// `Origin: streamdeck://` header, else a bare 401.
///
/// While the link is up the inventory is event-driven: the app's
/// `*Changed` notifications schedule a debounced re-read of the affected
/// lists, with a slow backstop poll in case one is missed. The app pushes
/// `levelMeterChanged` at ~20 Hz; each tick carries only the meters that
/// changed (output devices, channels and inputs unasked; mixes once
/// subscribed). Ticks are folded into a peak-hold
/// <see cref="WaveLinkMeterStore"/> that the consumer takes from at its own
/// pace. The app's `level*Percentage` fields are a dB scale: sample peak,
/// linear from -60 dBFS (0) to 0 dBFS (1) in 0.25 dB steps (see
/// <see cref="WaveLinkScale"/>); they are scaled to the 0..100 contract when taken.
/// </summary>
public sealed class WaveLinkCollector : IDisposable
{
    /// <summary>Fixed retry cadence between absent attempts.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    /// <summary>Notifications are coalesced for this long before the lists are re-read.</summary>
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(50);

    /// <summary>Full re-read when no notification asked for one (a missed event).</summary>
    private static readonly TimeSpan BackstopInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    /// <summary>No inbound frame for this long triggers a liveness ping.</summary>
    private const long SilenceLimitMs = 10_000;

    /// <summary>Consecutive RPC timeouts that end the attempt.</summary>
    private const int MaxRpcTimeouts = 3;

    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _stateGate = new();
    private readonly WaveLinkMeterStore _meters = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile WaveLinkState _state = new();
    private string _appVersion = "";
    private string _lastFailure = "";
    private bool _disposed;
    private bool _running;

    [Flags]
    private enum Dirty
    {
        None = 0,
        Devices = 1,
        Channels = 2,
        Mixes = 4,
        OutputDevices = 8,
        All = Devices | Channels | Mixes | OutputDevices,
    }

    /// <summary>The current inventory snapshot (reference-swapped, never mutated).</summary>
    public WaveLinkState State => _state;

    /// <summary>
    /// Takes the meters: one entry per live key holding the peak since the
    /// previous take (or the last value if no tick arrived), then starts a
    /// new peak window. Empty while absent.
    /// </summary>
    public void TakeMeters(List<WlMeterEntry> into) => _meters.Take(into);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _appVersion = DiscoverAppVersion();
        var ct = _cts.Token;
        _runTask = Task.Run(() => RunAsync(ct));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        var finished = true;
        if (_runTask != null)
        {
            try { finished = _runTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutdown */ }
        }
        // A run loop that outlived the wait still holds the token; leave the source to the GC.
        if (finished)
            _cts?.Dispose();
    }

    /* ---------------- the run loop ---------------- */

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WlAttempt? attempt = null;
            try
            {
                attempt = await TryOpenAsync(ct);
                if (attempt is not null)
                {
                    var token = attempt.Cts.Token;
                    // The read loop must be consuming before the first inventory
                    // RPC is sent — the responses are what complete them.
                    var readTask = Task.Run(() => ReadLoopAsync(attempt, token));
                    var refreshTask = Task.Run(() => RefreshLoopAsync(attempt, token));
                    var watchdogTask = Task.Run(() => WatchdogLoopAsync(attempt, token));

                    await Task.WhenAny(readTask, refreshTask, watchdogTask);
                    attempt.Cts.Cancel();
                    try { await Task.WhenAll(readTask, refreshTask, watchdogTask); } catch { /* attempt over */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailure($"attempt failed: {ex.Message}");
            }
            finally
            {
                attempt?.Dispose();
            }

            SetStatus("absent");
            try
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /* ---------------- connecting ---------------- */

    private async Task<WlAttempt?> TryOpenAsync(CancellationToken ct)
    {
        var port = ResolvePort();
        if (port is null)
            return null;

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "streamdeck://");
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        try
        {
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), connCts.Token);
            _lastFailure = "";
            _meters.Clear();
            SetStatus("up");
            return new WlAttempt(socket, port.Value, CancellationTokenSource.CreateLinkedTokenSource(ct));
        }
        catch (Exception ex)
        {
            LogFailure($"connect to port {port} failed: {ex.Message}");
            socket.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Explicit port override from the plugin's own config file, else the
    /// app's port file (absent = null).
    /// </summary>
    private static int? ResolvePort()
    {
        try
        {
            var path = WaveLinkPaths.ConfigFile("wavelink.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("port", out var p) &&
                    p.TryGetInt32(out var port) && port > 0)
                {
                    return port;
                }
            }
        }
        catch
        {
            // unreadable config — fall through to the port file
        }

        return DiscoverPort();
    }

    /// <summary>
    /// The app's own port file — it rewrites it on every launch (port
    /// conflicts), so it is the only source of truth. No file = the app is
    /// not running.
    /// </summary>
    private static int? DiscoverPort()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Elgato.WaveLink_g54w8ztgkx496", "LocalState", "ws-info.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("port", out var p) &&
                p.TryGetInt32(out var port) && port > 0)
            {
                return port;
            }
        }
        catch
        {
            // treat as absent
        }
        return null;
    }

    /// <summary>
    /// The app's own version (getApplicationInfo). The WindowsApps folder name is only a
    /// fallback: that folder is not listable without admin rights.
    /// </summary>
    private async Task ReadAppVersionAsync(WlAttempt a)
    {
        try
        {
            using var doc = JsonDocument.Parse(await RpcAsync(a, "getApplicationInfo"));
            if (doc.RootElement.TryGetProperty("version", out var v) &&
                v.ValueKind == JsonValueKind.String &&
                v.GetString() is { Length: > 0 } version &&
                version != _appVersion)
            {
                _appVersion = version;
                SetStatus(_state.Status);
            }
        }
        catch
        {
            // version is decorative — never block on it (a cancelled attempt ends in the loop below)
        }
    }

    /// <summary>The app version from its WindowsApps install name, when readable.</summary>
    private static string DiscoverAppVersion()
    {
        try
        {
            const string family = "Elgato.WaveLink_g54w8ztgkx496_";
            foreach (var dir in Directory.EnumerateDirectories(@"C:\Program Files\WindowsApps", family + "*"))
            {
                var name = Path.GetFileName(dir);
                var parts = name.Split('_');
                if (parts.Length > 1)
                    return parts[1];
            }
        }
        catch
        {
            // version is decorative — never block on it
        }
        return "";
    }


    /* ---------------- the read loop ---------------- */

    private async Task ReadLoopAsync(WlAttempt a, CancellationToken ct)
    {
        // One growable buffer for the whole attempt, reset per message.
        var buffer = new ArrayBufferWriter<byte>(16384);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await a.Socket.ReceiveAsync(buffer.GetMemory(8192), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    buffer.Advance(result.Count);
                    if (buffer.WrittenCount > MaxMessageBytes)
                        throw new InvalidDataException($"message exceeds {MaxMessageBytes} bytes");
                }
                while (!result.EndOfMessage);

                ProcessMessage(a, buffer.WrittenMemory);
            }
        }
        catch (OperationCanceledException) { /* attempt ended */ }
        catch (WebSocketException) { /* the app closed the link */ }
        catch (InvalidDataException ex)
        {
            LogFailure($"dropping link: {ex.Message}");
        }
    }

    private void ProcessMessage(WlAttempt a, ReadOnlyMemory<byte> message)
    {
        if (message.IsEmpty)
            return;

        var kind = WaveLinkWire.Classify(message.Span);
        if (kind == WlMessageKind.Invalid)
            return;

        var now = Environment.TickCount64;
        Volatile.Write(ref a.LastTrafficMs, now);

        switch (kind)
        {
            case WlMessageKind.LevelMeter:
                try
                {
                    WaveLinkWire.ReadMeters(message.Span, _meters);
                }
                catch (JsonException)
                {
                    // a malformed tick must never kill the read loop
                }
                break;
            case WlMessageKind.Response:
                HandleResponse(a, message);
                break;
            case WlMessageKind.Channels:
                MarkDirty(a, Dirty.Channels);
                break;
            case WlMessageKind.Mixes:
                // A channel's mix routes (level/mute per mix) live in the channel record.
                MarkDirty(a, Dirty.Mixes | Dirty.Channels);
                break;
            case WlMessageKind.InputDevices:
                MarkDirty(a, Dirty.Devices);
                break;
            case WlMessageKind.OutputDevices:
                MarkDirty(a, Dirty.OutputDevices);
                break;
        }
    }

    private static void HandleResponse(WlAttempt a, ReadOnlyMemory<byte> message)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number &&
                idProp.TryGetInt64(out var id) &&
                a.Pending.TryRemove(id, out var tcs))
            {
                if (root.TryGetProperty("error", out var err))
                    tcs.TrySetException(new InvalidOperationException("Wave Link RPC error: " + err.GetRawText()));
                else if (root.TryGetProperty("result", out var r))
                    tcs.TrySetResult(r.GetRawText());
                else
                    tcs.TrySetException(new InvalidOperationException("Wave Link RPC response without result"));
            }
        }
    }

    /* ---------------- event-driven inventory ---------------- */

    /// <summary>Flags lists for re-read; wakes the refresh loop only on the clean-to-dirty edge.</summary>
    private static void MarkDirty(WlAttempt a, Dirty lists)
    {
        if (Interlocked.Or(ref a.DirtyBits, (int)lists) == 0)
            a.Wake.Release();
    }

    private async Task RefreshLoopAsync(WlAttempt a, CancellationToken ct)
    {
        var pending = Dirty.All; // the connect-time full read
        await ReadAppVersionAsync(a);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(a, pending, ct);
                pending = Dirty.None;

                if (await a.Wake.WaitAsync(BackstopInterval, ct))
                {
                    await Task.Delay(RefreshDebounce, ct);
                    pending = (Dirty)Interlocked.Exchange(ref a.DirtyBits, 0);
                }
                else
                {
                    Interlocked.Exchange(ref a.DirtyBits, 0);
                    pending = Dirty.All;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailure($"inventory refresh failed: {ex.Message}");
                pending = Dirty.None;
            }
        }
    }

    private async Task RefreshAsync(WlAttempt a, Dirty lists, CancellationToken ct)
    {
        if (lists == Dirty.None)
            return;

        // Start the wanted reads together, then await them.
        var devRpc = lists.HasFlag(Dirty.Devices) ? RpcAsync(a, "getInputDevices") : null;
        var chRpc = lists.HasFlag(Dirty.Channels) ? RpcAsync(a, "getChannels") : null;
        var mixRpc = lists.HasFlag(Dirty.Mixes) ? RpcAsync(a, "getMixes") : null;
        var outRpc = lists.HasFlag(Dirty.OutputDevices) ? RpcAsync(a, "getOutputDevices") : null;

        List<WlDevice>? devices;
        List<WlChannel>? channels;
        List<WlMix>? mixes;
        List<WlOutputDevice>? outputs;
        try
        {
            devices = await TryReadAsync(a, devRpc, DeserializeDevices, "input devices");
            channels = await TryReadAsync(a, chRpc, DeserializeChannels, "channels");
            mixes = await TryReadAsync(a, mixRpc, DeserializeMixes, "mixes");
            outputs = await TryReadAsync(a, outRpc, DeserializeOutputDevices, "output devices");
        }
        finally
        {
            // Every started RPC settles before this pass (and the attempt) can end.
            foreach (var rpc in new[] { devRpc, chRpc, mixRpc, outRpc })
            {
                if (rpc is null) continue;
                try { await rpc; } catch { /* observed; already reported by TryReadAsync */ }
            }
        }
        ct.ThrowIfCancellationRequested();

        // Meters for mixes are opt-in; subscribe every mix once per connection.
        if (mixes is not null)
            await SubscribeMixesAsync(a, mixes);

        // One stalled pass counts as one timeout, however many of its RPCs stalled.
        if (Interlocked.Exchange(ref a.PassTimedOut, 0) == 1)
            NoteTimeout(a);

        if (devices is null && channels is null && mixes is null && outputs is null)
            return;

        var previous = (Devices: a.Devices, Channels: a.Channels, Mixes: a.Mixes, Outputs: a.OutputDevices);
        a.Devices = devices ?? a.Devices;
        a.Channels = channels ?? a.Channels;
        a.Mixes = mixes ?? a.Mixes;
        a.OutputDevices = outputs ?? a.OutputDevices;
        SetInventory(a.Devices, a.Channels, a.Mixes, a.OutputDevices);

        // Only an explicit removal (listed before, gone now) drops a meter slot.
        if (channels is not null)
            Prune(WlMeterKind.Channel, previous.Channels.Select(c => c.Id), channels.Select(c => c.Id));
        if (mixes is not null)
            Prune(WlMeterKind.Mix, previous.Mixes.Select(m => m.Id), mixes.Select(m => m.Id));
        if (outputs is not null)
            Prune(WlMeterKind.Device, previous.Outputs.Select(d => d.Id), outputs.Select(d => d.Id));
        if (devices is not null)
        {
            static IEnumerable<string> InputIds(List<WlDevice> list) =>
                list.SelectMany(d => d.Inputs.Select(i => i.Id).Append(d.Id));
            Prune(WlMeterKind.Input, InputIds(previous.Devices), InputIds(devices));
        }
    }

    private void Prune(WlMeterKind kind, IEnumerable<string> before, IEnumerable<string> now)
    {
        var removed = before.ToHashSet();
        removed.ExceptWith(now);
        _meters.Remove(kind, removed);
    }

    /// <summary>Opts each not-yet-subscribed mix into `levelMeterChanged` (channel/input types are not subscribable).</summary>
    private async Task SubscribeMixesAsync(WlAttempt a, List<WlMix> mixes)
    {
        foreach (var mix in mixes)
        {
            if (mix.Id.Length == 0 || a.SubscribedMixes.Contains(mix.Id))
                continue;
            try
            {
                var id = JsonSerializer.Serialize(mix.Id);
                await RpcAsync(a, "setSubscription",
                    $"{{\"levelMeterChanged\":{{\"type\":\"mix\",\"id\":{id},\"isEnabled\":true}}}}");
                a.SubscribedMixes.Add(mix.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is TimeoutException)
                {
                    Volatile.Write(ref a.PassTimedOut, 1);
                    LogFailure($"mix subscription timed out: {ex.Message}");
                }
                else
                {
                    // The app answered (an RPC error); don't re-ask every pass.
                    a.SubscribedMixes.Add(mix.Id);
                    LogFailure($"mix subscription failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>One list's read; a failure is logged and leaves that list as it was.</summary>
    private async Task<T?> TryReadAsync<T>(WlAttempt a, Task<string>? rpc, Func<string, T> parse, string what) where T : class
    {
        if (rpc is null)
            return null;
        try
        {
            return parse(await rpc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is TimeoutException)
                Volatile.Write(ref a.PassTimedOut, 1);
            LogFailure($"{what} read failed: {ex.Message}");
            return null;
        }
    }

    /* ---------------- liveness ---------------- */

    /// <summary>
    /// Ends the attempt when the app goes silent and no longer answers a ping
    /// (socket open but app hung) — the run loop then reconnects.
    /// </summary>
    private async Task WatchdogLoopAsync(WlAttempt a, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogInterval, ct);
                if (Environment.TickCount64 - Volatile.Read(ref a.LastTrafficMs) < SilenceLimitMs)
                    continue;
                await RpcAsync(a, "getApplicationInfo");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
                LogFailure("no traffic and the liveness ping timed out");
                NoteTimeout(a);
            }
            catch (Exception ex)
            {
                // An RPC error still proves the app is alive.
                LogFailure($"liveness ping failed: {ex.Message}");
            }
        }
    }

    /* ---------------- RPC ---------------- */

    private async Task<string> RpcAsync(WlAttempt a, string method, string? paramsJson = null)
    {
        var token = a.Cts.Token;
        var id = Interlocked.Increment(ref a.NextRpcId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Pending[id] = tcs;
        try
        {
            var args = paramsJson is null ? "" : $",\"params\":{paramsJson}";
            await SendAsync(a, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"{args}}}", token);

            var raw = await tcs.Task.WaitAsync(RpcTimeout, token);
            Interlocked.Exchange(ref a.Timeouts, 0);
            return raw;
        }
        finally
        {
            a.Pending.TryRemove(id, out _);
        }
    }

    /// <summary>Counts one stalled operation (a refresh pass or a ping); enough in a row ends the attempt.</summary>
    private void NoteTimeout(WlAttempt a)
    {
        if (Interlocked.Increment(ref a.Timeouts) >= MaxRpcTimeouts)
        {
            LogFailure($"{MaxRpcTimeouts} stalled operations in a row; reconnecting");
            a.Cts.Cancel();
        }
    }

    private static async Task SendAsync(WlAttempt a, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        // A blocked send must not wedge SendLock (and every loop queued behind it).
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(RpcTimeout);
        await a.SendLock.WaitAsync(sendCts.Token);
        try
        {
            if (a.Socket.State == WebSocketState.Open)
                await a.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendCts.Token);
        }
        finally
        {
            a.SendLock.Release();
        }
    }

    /* ---------------- state publishing ---------------- */

    private void SetStatus(string status)
    {
        lock (_stateGate)
        {
            var previous = _state;
            if (previous.Status != status)
                Console.WriteLine($"[WaveLink] link {status}");
            _state = new WaveLinkState
            {
                Status = status,
                AppVersion = _appVersion,
                Devices = previous.Devices,
                Channels = previous.Channels,
                Mixes = previous.Mixes,
                OutputDevices = previous.OutputDevices,
            };
        }
        if (status != "up")
            _meters.Clear();
    }

    private void SetInventory(List<WlDevice> devices, List<WlChannel> channels, List<WlMix> mixes, List<WlOutputDevice> outputDevices)
    {
        lock (_stateGate)
        {
            var previous = _state;
            _state = new WaveLinkState
            {
                Status = previous.Status,
                AppVersion = previous.AppVersion,
                Devices = devices,
                Channels = channels,
                Mixes = mixes,
                OutputDevices = outputDevices,
            };
        }
    }

    /// <summary>Logs a failure once per distinct message until a connect succeeds — never per tick or per retry.</summary>
    private void LogFailure(string message)
    {
        if (message == _lastFailure)
            return;
        _lastFailure = message;
        Console.WriteLine($"[WaveLink] {message}");
    }

    /* ---------------- normalization (mirrors the f3nn3x adapter) ---------------- */

    private static List<WlDevice> DeserializeDevices(string raw)
    {
        var p = JsonSerializer.Deserialize<WlInputDevicesPayload>(raw, JsonOpts);
        return (p?.InputDevices ?? new List<WlDeviceRaw>())
            .Select(d => new WlDevice(
                d.Id ?? "",
                d.Name ?? "",
                d.DeviceType ?? "",
                (d.Inputs ?? new List<WlInputRaw>())
                    .Select(i => new WlInput(
                        i.Id ?? "",
                        i.Name ?? "",
                        i.IsMuted,
                        i.Gain is { } g ? new WlGain(g.Value, g.Min, g.Max,
                            g.Value is { } gv ? WaveLinkScale.GainDb(gv, g.LookUpTable) : null) : null))
                    .ToList()))
            .ToList();
    }

    private static List<WlChannel> DeserializeChannels(string raw)
    {
        var p = JsonSerializer.Deserialize<WlChannelsPayload>(raw, JsonOpts);
        return (p?.Channels ?? new List<WlChannelRaw>())
            .Select(c => new WlChannel(
                c.Id ?? "",
                c.Name ?? "",
                c.Type ?? "",
                c.Level,
                c.IsMuted,
                (c.Mixes ?? new List<WlMixRouteRaw>())
                    .Select(m => new WlMixRoute(m.Id ?? "", m.Level, m.IsMuted,
                        m.Level is { } sl ? WaveLinkScale.MixSendDb(sl) : null))
                    .ToList(),
                (c.Apps ?? new List<WlAppRaw>())
                    .Select(ap => new WlApp(ap.Id ?? "", ap.Name ?? ""))
                    .ToList(),
                c.Level is { } cl ? WaveLinkScale.ChannelFaderDb(cl) : null))
            .ToList();
    }

    private static List<WlMix> DeserializeMixes(string raw)
    {
        var p = JsonSerializer.Deserialize<WlMixesPayload>(raw, JsonOpts);
        return (p?.Mixes ?? new List<WlMixRaw>())
            .Select(m => new WlMix(m.Id ?? "", m.Name ?? "", m.Level, m.IsMuted,
                m.Level is { } ml ? WaveLinkScale.MixMasterDb(ml) : null))
            .ToList();
    }

    private static List<WlOutputDevice> DeserializeOutputDevices(string raw)
    {
        var p = JsonSerializer.Deserialize<WlOutputDevicesPayload>(raw, JsonOpts);
        return (p?.OutputDevices ?? new List<WlOutputDeviceRaw>())
            .Select(d => new WlOutputDevice(
                d.Id ?? "",
                d.Name ?? "",
                d.DeviceType ?? "",
                (d.Outputs ?? new List<WlOutputRaw>())
                    .Select(o => new WlOutput(o.Id ?? "", o.Name ?? "", o.IsMuted, o.Level, o.MixId ?? ""))
                    .ToList()))
            .ToList();
    }

    /* ---------------- meter key namespacing (ported from shared/catalog.js) ---------------- */

    /// <summary>Device meter keys (`device:{MMDevice id}`) and channel meter keys (`channel:{PCM token}`)
    /// live in one map, kept apart by name.</summary>
    public static string DeviceKey(string id) => "device:" + id;
    public static string ChannelKey(string id) => "channel:" + id;
    public static string InputKey(string id) => "input:" + id;
    public static string MixKey(string id) => "mix:" + id;

    public static string MeterKey(WlMeterKind kind, string id) => kind switch
    {
        WlMeterKind.Device => DeviceKey(id),
        WlMeterKind.Channel => ChannelKey(id),
        WlMeterKind.Input => InputKey(id),
        _ => MixKey(id),
    };

    /* ---------------- the attempt (one connect session) ---------------- */

    private sealed class WlAttempt : IDisposable
    {
        public WlAttempt(ClientWebSocket socket, int port, CancellationTokenSource cts)
        {
            Socket = socket;
            Port = port;
            Cts = cts;
        }

        public ClientWebSocket Socket { get; }
        public int Port { get; }

        /// <summary>The attempt's token source; cancelling it ends every loop of the attempt.</summary>
        public CancellationTokenSource Cts { get; }
        public ConcurrentDictionary<long, TaskCompletionSource<string>> Pending { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public SemaphoreSlim Wake { get; } = new(0);
        public long NextRpcId;
        public long LastTrafficMs = Environment.TickCount64;
        public int DirtyBits;
        public int Timeouts;
        public int PassTimedOut;
        public List<WlDevice> Devices { get; set; } = new();
        public List<WlChannel> Channels { get; set; } = new();
        public List<WlMix> Mixes { get; set; } = new();
        public List<WlOutputDevice> OutputDevices { get; set; } = new();
        public HashSet<string> SubscribedMixes { get; } = new();

        public void Dispose()
        {
            try
            {
                Cts.Cancel();
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    Socket.Abort();
            }
            catch { /* already gone */ }

            foreach (var tcs in Pending.Values)
                tcs.TrySetCanceled();
            Pending.Clear();

            Socket.Dispose();
            SendLock.Dispose();
            Wake.Dispose();
            Cts.Dispose();
        }
    }

    /* ---------------- JSON-RPC wire payloads ---------------- */

    private sealed class WlInputDevicesPayload
    {
        public List<WlDeviceRaw>? InputDevices { get; set; }
    }

    private sealed class WlOutputDevicesPayload
    {
        public List<WlOutputDeviceRaw>? OutputDevices { get; set; }
    }

    private sealed class WlOutputDeviceRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? DeviceType { get; set; }
        public List<WlOutputRaw>? Outputs { get; set; }
    }

    private sealed class WlOutputRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool IsMuted { get; set; }
        public double? Level { get; set; }
        public string? MixId { get; set; }
    }

    private sealed class WlDeviceRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? DeviceType { get; set; }
        public List<WlInputRaw>? Inputs { get; set; }
    }

    private sealed class WlInputRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool IsMuted { get; set; }
        public WlGainRaw? Gain { get; set; }
    }

    private sealed class WlGainRaw
    {
        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        /// <summary>The app's own value → dB pairs; empty for a gain without a dB scale.</summary>
        public List<double[]>? LookUpTable { get; set; }
    }

    private sealed class WlChannelsPayload
    {
        public List<WlChannelRaw>? Channels { get; set; }
    }

    private sealed class WlChannelRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public double? Level { get; set; }
        public bool IsMuted { get; set; }
        public List<WlMixRouteRaw>? Mixes { get; set; }
        public List<WlAppRaw>? Apps { get; set; }
    }

    private sealed class WlMixRouteRaw
    {
        public string? Id { get; set; }
        public double? Level { get; set; }
        public bool IsMuted { get; set; }
    }

    private sealed class WlAppRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class WlMixesPayload
    {
        public List<WlMixRaw>? Mixes { get; set; }
    }

    private sealed class WlMixRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public double? Level { get; set; }
        public bool IsMuted { get; set; }
    }
}
