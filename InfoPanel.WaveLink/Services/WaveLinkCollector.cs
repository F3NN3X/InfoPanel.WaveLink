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
public sealed record WlGain(double? Value, double? Min, double? Max);

public sealed record WlInput(string Id, string Name, bool IsMuted, WlGain? Gain);

public sealed record WlDevice(string Id, string Name, string DeviceType, List<WlInput> Inputs);

/// <summary>A channel's route into an output mix, with the mix's level/mute.</summary>
public sealed record WlMixRoute(string Id, double? Level, bool IsMuted);

public sealed record WlApp(string Id, string Name);

public sealed record WlChannel(
    string Id,
    string Name,
    string Type,
    double? Level,
    bool IsMuted,
    List<WlMixRoute> Mixes,
    List<WlApp> Apps);

/// <summary>One of the app's output mixers (Mic Mix, Stream Mix, ...).</summary>
public sealed record WlMix(string Id, string Name, double? Level, bool IsMuted);

/// <summary>Left/right meter levels for one meter, 0..100 (100 = 0 dBFS).</summary>
public sealed record WlMeterLevels(double Left, double Right);

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
/// While the link is up the collector re-reads the channel/mix inventory on
/// a 1 s poll and publishes only on change; the app pushes
/// `levelMeterChanged` at device pace with no subscription, and each tick
/// carries the merged meter map whole (keys `device:{id}` /
/// `channel:{id}`, left/right percentages 0..100). The app's
/// `level*Percentage` fields are 0..1 fractions of full scale (1.0 = 0 dBFS)
/// despite their name; the collector normalizes them to the 0..100 contract.
/// </summary>
public sealed class WaveLinkCollector : IDisposable
{
    /// <summary>Fixed retry cadence between absent attempts.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly int RpcTimeoutMs = 5000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile WaveLinkState _state = new();
    private string _appVersion = "";
    private volatile Dictionary<string, WlMeterLevels> _meters = new();
    private bool _disposed;
    private bool _running;

    /// <summary>The current inventory snapshot (reference-swapped, never mutated).</summary>
    public WaveLinkState State => _state;

    /// <summary>The latest merged meter map (reference-swapped, never mutated); empty while absent.</summary>
    public Dictionary<string, WlMeterLevels> Meters => _meters;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _appVersion = DiscoverAppVersion();
        _runTask = Task.Run(RunAsync);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        if (_runTask != null)
        {
            try { _runTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutdown */ }
        }
        _cts?.Dispose();
    }

    /* ---------------- the run loop ---------------- */

    private async Task RunAsync()
    {
        while (!_cts!.IsCancellationRequested)
        {
            WlAttempt? attempt = null;
            try
            {
                attempt = await TryOpenAsync();
                if (attempt is null)
                {
                    SetStatus("absent");
                    await Task.Delay(RetryDelay, _cts.Token);
                    continue;
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                // The read loop must be consuming before the first inventory
                // RPC is sent — the responses are what complete them.
                var readTask = Task.Run(() => ReadLoopAsync(attempt, attemptCts.Token));
                try
                {
                    await ReadInitialInventoryAsync(attempt);
                }
                catch (Exception ex)
                {
                    // The link is up even if the first read fails — the poll
                    // loop fills the inventory from its own first pass.
                    System.Diagnostics.Debug.WriteLine($"[WaveLink] WaveLink initial inventory failed: {ex.Message}");
                }

                var pollTask = Task.Run(() => PollLoopAsync(attempt, attemptCts.Token));

                await Task.WhenAny(readTask, pollTask);
                attemptCts.Cancel();
                try { await Task.WhenAll(readTask, pollTask); } catch { /* attempt over */ }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WaveLink] WaveLink attempt failed: {ex.Message}");
            }
            finally
            {
                attempt?.Dispose();
            }

            SetStatus("absent");
            try
            {
                await Task.Delay(RetryDelay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /* ---------------- connecting ---------------- */

    private async Task<WlAttempt?> TryOpenAsync()
    {
        var port = ResolvePort();
        if (port is null)
            return null;

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "streamdeck://");
        try
        {
            using var connCts = new CancellationTokenSource(ConnectTimeout);
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), connCts.Token);
            SetStatus("up");
            return new WlAttempt(socket, port.Value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WaveLink] WaveLink connect to port {port} failed: {ex.Message}");
            socket.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The connect-time inventory read (all three, in parallel). Runs with
    /// the read loop already consuming — the responses complete these.
    /// </summary>
    private async Task ReadInitialInventoryAsync(WlAttempt a)
    {
        var devTask = RpcAsync(a, "getInputDevices");
        var chTask = RpcAsync(a, "getChannels");
        var mixTask = RpcAsync(a, "getMixes");
        var (devRaw, chRaw, mixRaw) = (await devTask, await chTask, await mixTask);

        a.Devices = DeserializeDevices(devRaw);
        a.Channels = DeserializeChannels(chRaw);
        a.Mixes = DeserializeMixes(mixRaw);
        a.LastInventoryJson = InventoryJson(a);

        SetInventory(a.Devices, a.Channels, a.Mixes);
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
        var buffer = new byte[65536];
        using var earlier = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                earlier.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await a.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.EndOfMessage)
                        break;
                    earlier.Write(buffer, 0, result.Count);
                }
                while (true);

                var message = new byte[(int)earlier.Length + result.Count];
                if (earlier.Length > 0)
                {
                    var earlierBytes = earlier.ToArray();
                    Buffer.BlockCopy(earlierBytes, 0, message, 0, (int)earlier.Length);
                }
                Buffer.BlockCopy(buffer, 0, message, (int)earlier.Length, result.Count);
                ProcessMessage(a, Encoding.UTF8.GetString(message));
            }
        }
        catch (OperationCanceledException) { /* attempt ended */ }
        catch (WebSocketException) { /* the app closed the link */ }
    }

    private void ProcessMessage(WlAttempt a, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // A notification (the app pushes `levelMeterChanged` at device pace).
            if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
            {
                if (m.GetString() == "levelMeterChanged" &&
                    root.TryGetProperty("params", out var p))
                {
                    HandleMeters(a, p);
                }
                return;
            }

            // An RPC response.
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

    /// <summary>
    /// Merges a meter tick into the attempt's map and publishes the whole
    /// merged map — the app sends no deltas, and a key that disappears
    /// (the app closed a channel) drops out of the next tick naturally.
    /// </summary>
    private void HandleMeters(WlAttempt a, JsonElement paramsEl)
    {
        try
        {
            var p = paramsEl.Deserialize<WlMeterParams>(JsonOpts) ?? new WlMeterParams();

            foreach (var d in p.OutputDevices ?? new List<WlMeterItem>())
                a.Meters[DeviceKey(d.Id ?? "")] = (Pct(d.LevelLeftPercentage), Pct(d.LevelRightPercentage));
            foreach (var c in p.Channels ?? new List<WlMeterItem>())
                a.Meters[ChannelKey(c.Id ?? "")] = (Pct(c.LevelLeftPercentage), Pct(c.LevelRightPercentage));

            // The app's `level*Percentage` fields are 0..1 fractions of full
            // scale (1.0 = 0 dBFS) — normalize to the 0..100 wire contract.
            // Measured live: a 0 dBFS full-scale sine reads ~0.77 on the
            // output device (RMS of the sine is 0.707), not 77.
            static double Pct(double? fraction) => (fraction ?? 0) * 100;

            var meters = new Dictionary<string, WlMeterLevels>(a.Meters.Count);
            foreach (var (key, levels) in a.Meters)
                meters[key] = new WlMeterLevels(levels.Item1, levels.Item2);

            _meters = meters;
        }
        catch
        {
            // a malformed tick must never kill the read loop
        }
    }

    /* ---------------- the 1 s inventory poll ---------------- */

    private async Task PollLoopAsync(WlAttempt a, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (chTask, mixTask) = (
                    RpcAsync(a, "getChannels"),
                    RpcAsync(a, "getMixes"));
                var (chRaw, mixRaw) = (await chTask, await mixTask);

                var channels = DeserializeChannels(chRaw);
                var mixes = DeserializeMixes(mixRaw);
                var json = InventoryJson(a.Devices, channels, mixes);
                if (json != a.LastInventoryJson)
                {
                    a.LastInventoryJson = json;
                    SetInventory(a.Devices, channels, mixes);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed poll (the app hiccuping) is not the end of the
                // link — the read loop's close is. Log and keep polling.
                System.Diagnostics.Debug.WriteLine($"[WaveLink] WaveLink poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /* ---------------- RPC ---------------- */

    private static async Task<string> RpcAsync(WlAttempt a, string method)
    {
        var id = Interlocked.Increment(ref a.NextRpcId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Pending[id] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method }, JsonOpts);
            await SendAsync(a, payload);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(RpcTimeoutMs));
            if (done != tcs.Task)
                throw new TimeoutException($"Wave Link RPC {method} timed out");
            return await tcs.Task;
        }
        finally
        {
            a.Pending.TryRemove(id, out _);
        }
    }

    private static async Task SendAsync(WlAttempt a, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await a.SendLock.WaitAsync();
        try
        {
            if (a.Socket.State == WebSocketState.Open)
                await a.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            a.SendLock.Release();
        }
    }

    /* ---------------- state publishing ---------------- */

    private void SetStatus(string status)
    {
        var previous = _state;
        var next = new WaveLinkState
        {
            Status = status,
            AppVersion = _appVersion,
            Devices = previous.Devices,
            Channels = previous.Channels,
            Mixes = previous.Mixes,
        };
        _state = next;
        if (status != "up")
            _meters = new Dictionary<string, WlMeterLevels>();
    }

    private void SetInventory(List<WlDevice> devices, List<WlChannel> channels, List<WlMix> mixes)
    {
        var previous = _state;
        var next = new WaveLinkState
        {
            Status = previous.Status,
            AppVersion = previous.AppVersion,
            Devices = devices,
            Channels = channels,
            Mixes = mixes,
        };
        _state = next;
    }

    /* ---------------- normalization (mirrors the f3nn3x adapter) ---------------- */

    private static string InventoryJson(WlAttempt a) => InventoryJson(a.Devices, a.Channels, a.Mixes);

    private static string InventoryJson(List<WlDevice> devices, List<WlChannel> channels, List<WlMix> mixes)
    {
        return JsonSerializer.Serialize(new { devices, channels, mixes }, JsonOpts);
    }

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
                        i.Gain is { } g ? new WlGain(g.Value, g.Min, g.Max) : null))
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
                    .Select(m => new WlMixRoute(m.Id ?? "", m.Level, m.IsMuted))
                    .ToList(),
                (c.Apps ?? new List<WlAppRaw>())
                    .Select(ap => new WlApp(ap.Id ?? "", ap.Name ?? ""))
                    .ToList()))
            .ToList();
    }

    private static List<WlMix> DeserializeMixes(string raw)
    {
        var p = JsonSerializer.Deserialize<WlMixesPayload>(raw, JsonOpts);
        return (p?.Mixes ?? new List<WlMixRaw>())
            .Select(m => new WlMix(m.Id ?? "", m.Name ?? "", m.Level, m.IsMuted))
            .ToList();
    }

    /* ---------------- meter key namespacing (ported from shared/catalog.js) ---------------- */

    /// <summary>Device meter keys (`device:{MMDevice id}`) and channel meter keys (`channel:{PCM token}`)
    /// live in one map, kept apart by name.</summary>
    public static string DeviceKey(string id) => "device:" + id;
    public static string ChannelKey(string id) => "channel:" + id;

    /* ---------------- the attempt (one connect session) ---------------- */

    private sealed class WlAttempt : IDisposable
    {
        public WlAttempt(ClientWebSocket socket, int port)
        {
            Socket = socket;
            Port = port;
        }

        public ClientWebSocket Socket { get; }
        public int Port { get; }
        public ConcurrentDictionary<long, TaskCompletionSource<string>> Pending { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, (double Left, double Right)> Meters { get; } = new();
        public long NextRpcId;
        public List<WlDevice> Devices { get; set; } = new();
        public List<WlChannel> Channels { get; set; } = new();
        public List<WlMix> Mixes { get; set; } = new();
        public string LastInventoryJson { get; set; } = "";

        public void Dispose()
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    Socket.Abort();
            }
            catch { /* already gone */ }

            foreach (var tcs in Pending.Values)
                tcs.TrySetCanceled();
            Pending.Clear();

            Socket.Dispose();
            SendLock.Dispose();
        }
    }

    /* ---------------- JSON-RPC wire payloads ---------------- */

    private sealed class WlInputDevicesPayload
    {
        public List<WlDeviceRaw>? InputDevices { get; set; }
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

    private sealed class WlMeterParams
    {
        public List<WlMeterItem>? OutputDevices { get; set; }
        public List<WlMeterItem>? Channels { get; set; }
    }

    private sealed class WlMeterItem
    {
        public string? Id { get; set; }
        public double? LevelLeftPercentage { get; set; }
        public double? LevelRightPercentage { get; set; }
    }
}
