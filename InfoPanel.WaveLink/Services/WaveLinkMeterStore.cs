namespace InfoPanel.WaveLink.Services;

/// <summary>What a meter slot measures; selects the published key prefix.</summary>
public enum WlMeterKind
{
    Device,
    Channel,
    Input,
    Mix,
}

/// <summary>
/// One published meter. <see cref="From"/> is the single place the app's 0..1
/// fractions become published values: 0..100 percentages plus dBFS
/// (<see cref="WaveLinkScale.MeterDb"/>, null for silence).
/// </summary>
public readonly record struct WlMeterEntry(string Key, double Left, double Right, double? LeftDb, double? RightDb)
{
    public static WlMeterEntry From(string key, double leftFraction, double rightFraction) =>
        new(key, leftFraction * 100, rightFraction * 100,
            WaveLinkScale.MeterDb(leftFraction), WaveLinkScale.MeterDb(rightFraction));
}

/// <summary>
/// Per-meter peak-hold slots. The 20 Hz read loop writes ticks in; the
/// plugin's slower update takes-and-resets. A slot keeps the max level seen
/// since the last take, so peaks between two reads are not lost; with no tick
/// since the last take it publishes the last value (zero only if the app sent
/// zero). The app sends only meters that changed, so a steady (silent, muted,
/// held) meter is never re-sent: slots never expire by time, only on an
/// explicit inventory removal or a disconnect. Slots are preallocated per key and mutated in place under a
/// tiny lock — the hot path allocates nothing once a key is known.
/// </summary>
public sealed class WaveLinkMeterStore
{
    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();

    public void Update(WlMeterKind kind, ReadOnlySpan<byte> id, double? left, double? right)
    {
        lock (_gate)
        {
            var slot = Find(kind, id) ?? Add(kind, id);
            // A tick carrying only one side keeps the other at its current value.
            var l = left ?? slot.LastLeft;
            var r = right ?? slot.LastRight;
            slot.LastLeft = l;
            slot.LastRight = r;
            if (slot.Fresh)
            {
                if (l > slot.PeakLeft) slot.PeakLeft = l;
                if (r > slot.PeakRight) slot.PeakRight = r;
            }
            else
            {
                slot.PeakLeft = l;
                slot.PeakRight = r;
                slot.Fresh = true;
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="into"/> with one entry per known key (peak since
    /// the last take, else the last value) and starts a new peak window.
    /// </summary>
    public void Take(List<WlMeterEntry> into)
    {
        into.Clear();
        lock (_gate)
        {
            foreach (var s in _slots)
            {
                into.Add(s.Fresh
                    ? WlMeterEntry.From(s.Key, s.PeakLeft, s.PeakRight)
                    : WlMeterEntry.From(s.Key, s.LastLeft, s.LastRight));
                s.Fresh = false;
            }
        }
    }

    /// <summary>
    /// Drops slots of one kind whose ids the inventory explicitly removed (present
    /// in the previous snapshot, absent from the new one). Never by quiet time.
    /// </summary>
    public void Remove(WlMeterKind kind, HashSet<string> removedIds)
    {
        if (removedIds.Count == 0)
            return;
        lock (_gate)
        {
            _slots.RemoveAll(s => s.Kind == kind && removedIds.Contains(s.Id));
        }
    }

    public void Clear()
    {
        lock (_gate)
            _slots.Clear();
    }

    private Slot? Find(WlMeterKind kind, ReadOnlySpan<byte> id)
    {
        foreach (var s in _slots)
        {
            if (s.Kind == kind && id.SequenceEqual(s.IdUtf8))
                return s;
        }
        return null;
    }

    private Slot Add(WlMeterKind kind, ReadOnlySpan<byte> id)
    {
        var text = System.Text.Encoding.UTF8.GetString(id);
        var slot = new Slot
        {
            Kind = kind,
            IdUtf8 = id.ToArray(),
            Id = text,
            Key = WaveLinkCollector.MeterKey(kind, text),
        };
        _slots.Add(slot);
        return slot;
    }

    private sealed class Slot
    {
        public WlMeterKind Kind;
        public byte[] IdUtf8 = Array.Empty<byte>();
        public string Id = "";
        public string Key = "";
        public double LastLeft, LastRight;
        public double PeakLeft, PeakRight;
        public bool Fresh;
    }
}
