using System.Buffers;
using System.Text;
using System.Text.Json;
using InfoPanel.Plugins;
using InfoPanel.WaveLink.Services;

namespace InfoPanel.WaveLink;

/// <summary>
/// Exposes the Wave Link collector as four fixed text entries. The host fixes
/// a plugin's entries at Load, so the dynamic inventory and meters travel as
/// JSON strings.
/// </summary>
public class WaveLinkPlugin : BasePlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private WaveLinkCollector? _collector;
    private WaveLinkState? _lastState;

    // Reused every tick so the 10 Hz meter publish allocates nothing unless the text changed.
    private readonly object _meterGate = new();
    private readonly List<WlMeterEntry> _entries = new();
    private readonly Utf8JsonWriter _meterWriter = new(Stream.Null);
    private ArrayBufferWriter<byte> _meterJson = new();
    private ArrayBufferWriter<byte> _lastMeterJson = new();

    private PluginText? _status;
    private PluginText? _appVersion;
    private PluginText? _state;
    private PluginText? _meters;

    public WaveLinkPlugin()
        : base("wavelink-plugin", "Wave Link", "Elgato Wave Link inventory and live meters as JSON text")
    {
    }

    // Matches the host's ~100 ms forwarding cadence. Faster would not help: each Update
    // publishes the peak since the previous one, so a 50 ms window the host then samples
    // at 100 ms would drop every other window's peak.
    public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(100);

    public override void Initialize()
    {
        try
        {
            _collector = new WaveLinkCollector();
            _collector.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WaveLink] Failed to start collector: {ex.Message}");
            _collector = null;
        }
    }

    public override void Load(List<IPluginContainer> containers)
    {
        var container = new PluginContainer("wavelink", "Wave Link");
        container.Entries.Add(_status = new PluginText("status", "Status", "absent"));
        container.Entries.Add(_appVersion = new PluginText("app_version", "App Version", ""));
        container.Entries.Add(_state = new PluginText("state", "State", Serialize(new WaveLinkState())));
        container.Entries.Add(_meters = new PluginText("meters", "Meters", "{}"));
        containers.Add(container);
    }

    public override void Update()
    {
        var collector = _collector;
        if (collector is null || _status is null || _appVersion is null || _state is null || _meters is null)
            return;

        var state = collector.State;
        if (!ReferenceEquals(state, _lastState))
        {
            _lastState = state;
            _status.Value = state.Status;
            _appVersion.Value = state.Status == "up" ? state.AppVersion : "";

            // A new snapshot is built on every status call; resend only when the JSON differs.
            var json = Serialize(state);
            if (json != _state.Value)
                _state.Value = json;
        }

        lock (_meterGate)
        {
            collector.TakeMeters(_entries);
            WriteMeters();
            if (!_meterJson.WrittenSpan.SequenceEqual(_lastMeterJson.WrittenSpan))
            {
                _meters.Value = Encoding.UTF8.GetString(_meterJson.WrittenSpan);
                (_meterJson, _lastMeterJson) = (_lastMeterJson, _meterJson);
            }
        }
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        Update();
        return Task.CompletedTask;
    }

    public override void Close()
    {
        // Detach first so a concurrent Update can't reach a disposed collector.
        var collector = _collector;
        _collector = null;
        collector?.Dispose();
    }

    /// <summary>
    /// Writes `{ "device:{id}": {"left","right"}, ... }` into <see cref="_meterJson"/>,
    /// values 0..100 rounded to 2 decimals so an unchanged level yields identical bytes.
    /// dB values (null = silence) come precomputed with each entry.
    /// </summary>
    private void WriteMeters()
    {
        _meterJson.ResetWrittenCount();
        _meterWriter.Reset(_meterJson);
        _meterWriter.WriteStartObject();
        foreach (var m in _entries)
        {
            _meterWriter.WriteStartObject(m.Key);
            _meterWriter.WriteNumber("left"u8, Math.Round(m.Left, 2));
            _meterWriter.WriteNumber("right"u8, Math.Round(m.Right, 2));
            WriteDb("leftDb"u8, m.LeftDb);
            WriteDb("rightDb"u8, m.RightDb);
            _meterWriter.WriteEndObject();
        }
        _meterWriter.WriteEndObject();
        _meterWriter.Flush();
    }

    private void WriteDb(ReadOnlySpan<byte> name, double? db)
    {
        if (db is { } v)
            _meterWriter.WriteNumber(name, v);
        else
            _meterWriter.WriteNull(name);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);
}
