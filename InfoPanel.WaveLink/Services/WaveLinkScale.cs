namespace InfoPanel.WaveLink.Services;

/// <summary>
/// Wave Link's level scales as dB. Every formula is fitted to Wave Link 3.4
/// measurements (test tones), +-0.2 dB on a 0.1..1 level; below 0.1 they are
/// extrapolated. A level of 0 (or less) is silence/-inf and yields null, never
/// a finite floor. Results are rounded to 2 decimals.
/// </summary>
public static class WaveLinkScale
{
    /// <summary>
    /// A meter's `level*Percentage` (0..1) as dBFS: 60 * p - 60, identical for
    /// channel, output-device, mix and input-device meters (verified -58..0 dBFS
    /// within 0.03 dB; sample peak). Null at p &lt;= 0.
    /// </summary>
    public static double? MeterDb(double fraction) =>
        fraction <= 0 ? null : Round(60 * fraction - 60);

    /// <summary>Channel fader (`channel.level`) as gain dB: -84 * (1 - level^0.245).</summary>
    public static double? ChannelFaderDb(double level) =>
        level <= 0 ? null : Round(-84 * (1 - Math.Pow(level, 0.245)));

    /// <summary>Channel-to-mix send (`channel.mixes[].level`) as gain dB: -171 * (1 - level^0.22).</summary>
    public static double? MixSendDb(double level) =>
        level <= 0 ? null : Round(-171 * (1 - Math.Pow(level, 0.22)));

    /// <summary>Mix master (`mix.level`, the real signal change) as gain dB: 34.8 * log10(level).</summary>
    public static double? MixMasterDb(double level) =>
        level <= 0 ? null : Round(34.8 * Math.Log10(level));

    /// <summary>
    /// Input gain in dB from the app's own `lookUpTable` ([value, dB] pairs, sorted by value),
    /// interpolated linearly. A Wave Mic's table is value * 40 dB in 1 dB steps. Null when the
    /// table is empty: that gain (e.g. a third-party interface, max 1) has no dB scale.
    /// </summary>
    public static double? GainDb(double value, IReadOnlyList<double[]>? table)
    {
        if (table is null || table.Count == 0 || table.Any(p => p.Length < 2)) return null;
        if (value <= table[0][0]) return Round(table[0][1]);
        for (var i = 1; i < table.Count; i++)
        {
            var (x0, y0, x1, y1) = (table[i - 1][0], table[i - 1][1], table[i][0], table[i][1]);
            if (value <= x1)
                return Round(x1 > x0 ? y0 + (value - x0) / (x1 - x0) * (y1 - y0) : y1);
        }
        return Round(table[^1][1]);
    }

    // + 0.0 turns a -0 (fader at 1.0) into 0 so it never serializes as "-0"
    private static double Round(double db) => Math.Round(db, 2) + 0.0;
}
