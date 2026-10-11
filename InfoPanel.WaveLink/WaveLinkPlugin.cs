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
    private Dictionary<string, WlMeterLevels>? _lastMeters;

    private PluginText? _status;
    private PluginText? _appVersion;
    private PluginText? _state;
    private PluginText? _meters;

    public WaveLinkPlugin()
        : base("wavelink-plugin", "Wave Link", "Elgato Wave Link inventory and live meters as JSON text")
    {
    }

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
            System.Diagnostics.Debug.WriteLine($"[WaveLink] Failed to start collector: {ex.Message}");
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

        var meters = collector.Meters;
        if (!ReferenceEquals(meters, _lastMeters))
        {
            _lastMeters = meters;
            _meters.Value = Serialize(meters);
        }
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        Update();
        return Task.CompletedTask;
    }

    public override void Close()
    {
        _collector?.Dispose();
        _collector = null;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);
}
