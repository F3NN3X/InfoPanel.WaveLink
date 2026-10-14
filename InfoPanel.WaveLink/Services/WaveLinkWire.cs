using System.Text.Json;

namespace InfoPanel.WaveLink.Services;

/// <summary>What an inbound frame is, decided without materializing it.</summary>
internal enum WlMessageKind
{
    Invalid,
    Response,
    LevelMeter,
    Channels,
    Mixes,
    InputDevices,
    OutputDevices,
    OtherNotification,
}

/// <summary>
/// Allocation-free reading of the app's JSON-RPC frames straight from UTF-8
/// bytes. The 20 Hz `levelMeterChanged` path never builds strings or objects
/// for known meter keys; RPC responses (rare) are parsed by the collector.
/// </summary>
internal static class WaveLinkWire
{
    /// <summary>Longest meter id (UTF-8 bytes) read; longer ids are skipped.</summary>
    private const int MaxIdBytes = 256;

    /// <summary>Classifies a frame by its top-level `method` (none = an RPC response).</summary>
    public static WlMessageKind Classify(ReadOnlySpan<byte> msg)
    {
        try
        {
            var reader = new Utf8JsonReader(msg);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return WlMessageKind.Invalid;

            var kind = WlMessageKind.Response;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("method"u8))
                {
                    reader.Read();
                    kind = reader.TokenType == JsonTokenType.String
                        ? KindOf(ref reader)
                        : WlMessageKind.Invalid;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            return kind;
        }
        catch (JsonException)
        {
            return WlMessageKind.Invalid;
        }
    }

    private static WlMessageKind KindOf(ref Utf8JsonReader r)
    {
        if (r.ValueTextEquals("levelMeterChanged"u8)) return WlMessageKind.LevelMeter;
        if (r.ValueTextEquals("channelsChanged"u8) || r.ValueTextEquals("channelChanged"u8)) return WlMessageKind.Channels;
        if (r.ValueTextEquals("mixesChanged"u8) || r.ValueTextEquals("mixChanged"u8)) return WlMessageKind.Mixes;
        if (r.ValueTextEquals("inputDevicesChanged"u8) || r.ValueTextEquals("inputDeviceChanged"u8)) return WlMessageKind.InputDevices;
        if (r.ValueTextEquals("outputDevicesChanged"u8) || r.ValueTextEquals("outputDeviceChanged"u8)) return WlMessageKind.OutputDevices;
        return WlMessageKind.OtherNotification;
    }

    /// <summary>
    /// Feeds a `levelMeterChanged` frame into the store: `params.outputDevices[]`,
    /// `channels[]`, `inputDevices[]` (with `subId`) and `mixes[]` of `{id, levelLeftPercentage, levelRightPercentage}`
    /// (0..1 on a dB scale: -60 dBFS at 0, 0 dBFS at 1; see <see cref="WaveLinkScale.MeterDb"/>).
    /// </summary>
    public static void ReadMeters(ReadOnlySpan<byte> msg, WaveLinkMeterStore store)
    {
        Span<byte> idBuf = stackalloc byte[MaxIdBytes * 2]; // id, then subId
        var reader = new Utf8JsonReader(msg);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isParams = reader.ValueTextEquals("params"u8);
            reader.Read();
            if (isParams && reader.TokenType == JsonTokenType.StartObject)
                ReadParams(ref reader, store, idBuf);
            else
                reader.Skip();
        }
    }

    private static void ReadParams(ref Utf8JsonReader reader, WaveLinkMeterStore store, scoped Span<byte> idBuf)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            WlMeterKind? kind =
                reader.ValueTextEquals("outputDevices"u8) ? WlMeterKind.Device :
                reader.ValueTextEquals("channels"u8) ? WlMeterKind.Channel :
                reader.ValueTextEquals("inputDevices"u8) ? WlMeterKind.Input :
                reader.ValueTextEquals("mixes"u8) ? WlMeterKind.Mix : null;
            reader.Read();
            if (kind is { } k && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    ReadItem(ref reader, store, k, idBuf);
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadItem(ref Utf8JsonReader reader, WaveLinkMeterStore store, WlMeterKind kind, scoped Span<byte> bufs)
    {
        var idBuf = bufs[..MaxIdBytes];
        var subBuf = bufs[MaxIdBytes..];
        var idLen = -1;
        var subLen = -1;
        double? left = null, right = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isId = reader.ValueTextEquals("id"u8);
            var isSub = !isId && reader.ValueTextEquals("subId"u8);
            var isLeft = !isId && !isSub && reader.ValueTextEquals("levelLeftPercentage"u8);
            var isRight = !isId && !isSub && !isLeft && reader.ValueTextEquals("levelRightPercentage"u8);
            reader.Read();

            if ((isId || isSub) && reader.TokenType == JsonTokenType.String)
            {
                var dst = isId ? idBuf : subBuf;
                if (reader.ValueSpan.Length <= dst.Length)
                {
                    var len = reader.ValueIsEscaped
                        ? reader.CopyString(dst)
                        : CopyRaw(reader.ValueSpan, dst);
                    if (isId) idLen = len; else subLen = len;
                }
            }
            else if (isLeft && reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetDouble(out var v)) left = Math.Clamp(v, 0, 1);
            }
            else if (isRight && reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetDouble(out var v)) right = Math.Clamp(v, 0, 1);
            }
            else
            {
                reader.Skip();
            }
        }

        // An input meter is keyed by its subId (the id when it has none).
        if (kind == WlMeterKind.Input && subLen >= 0)
            store.Update(kind, subBuf[..subLen], left, right);
        else if (idLen >= 0)
            store.Update(kind, idBuf[..idLen], left, right);
    }

    private static int CopyRaw(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        src.CopyTo(dst);
        return src.Length;
    }
}
