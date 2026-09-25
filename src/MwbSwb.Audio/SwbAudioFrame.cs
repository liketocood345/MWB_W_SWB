using System.Buffers.Binary;

namespace MwbSwb.Audio;

/// <summary>12-byte SWB audio frame header + PCM16 stereo payload.</summary>
public static class SwbAudioFrame
{
    public const ushort Magic = 0x5753; // 'SW'
    public const byte Version = 1;
    public const int HeaderSize = 12;
    /// <summary>VM / LAN clock skew often exceeds 2s; keep wide enough that mesh audio is not all-dropped.</summary>
    public const int StaleSecDefault = 600;

    public readonly record struct Header(byte Tier, bool Rate24k, bool Plc, ushort Seq, uint TsSec);

    public static byte PackFlags(int tier, bool rate24k, bool plc)
    {
        tier = AudioTier.Clamp(tier);
        byte f = (byte)((tier & 0x7) << 5);
        if (rate24k) f |= 1 << 4;
        if (plc) f |= 1 << 3;
        return f;
    }

    public static void UnpackFlags(byte flags, out int tier, out bool rate24k, out bool plc)
    {
        tier = (flags >> 5) & 0x7;
        rate24k = (flags & (1 << 4)) != 0;
        plc = (flags & (1 << 3)) != 0;
    }

    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    public static byte[] Encode(Header hdr, ReadOnlySpan<short> pcm16Stereo)
    {
        var payloadBytes = pcm16Stereo.Length * sizeof(short);
        var packet = new byte[HeaderSize + payloadBytes];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Magic);
        span[2] = PackFlags(hdr.Tier, hdr.Rate24k, hdr.Plc);
        span[3] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], hdr.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(span[6..], hdr.TsSec);
        var crc = Crc16Ccitt(span.Slice(2, 8));
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], crc);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm16Stereo).CopyTo(span[HeaderSize..]);
        return packet;
    }

    public static bool TryDecode(byte[] packet, out Header hdr, out short[] pcm16)
    {
        hdr = default;
        pcm16 = Array.Empty<short>();
        if (packet.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet) != Magic) return false;
        if (packet[3] != Version) return false;
        var expectCrc = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10));
        var actualCrc = Crc16Ccitt(packet.AsSpan(2, 8));
        if (expectCrc != actualCrc) return false;

        UnpackFlags(packet[2], out var tier, out var rate24k, out var plc);
        if (tier > AudioTier.Max) return false;
        if (rate24k && tier != 6) return false;

        var seq = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4));
        var ts = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6));
        var payloadLen = packet.Length - HeaderSize;
        if (payloadLen % 4 != 0 || payloadLen == 0) return false;

        hdr = new Header((byte)tier, rate24k, plc, seq, ts);
        pcm16 = new short[payloadLen / sizeof(short)];
        Buffer.BlockCopy(packet, HeaderSize, pcm16, 0, payloadLen);
        return true;
    }

    public static bool IsStale(uint tsSec, int staleSec = StaleSecDefault)
    {
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var age = (long)now - (long)tsSec;
        // Drop only clearly ancient or wildly future packets (guest clocks drift).
        if (age > staleSec) return true;
        if (age < -staleSec) return true;
        return false;
    }

    public static void FloatToPcm16(ReadOnlySpan<float> floats, Span<short> pcm)
    {
        var n = Math.Min(floats.Length, pcm.Length);
        for (var i = 0; i < n; i++)
        {
            var s = floats[i];
            if (s > 1f) s = 1f;
            else if (s < -1f) s = -1f;
            pcm[i] = (short)(s * 32767f);
        }
    }

    public static void Pcm16ToFloat(ReadOnlySpan<short> pcm, Span<float> floats)
    {
        var n = Math.Min(pcm.Length, floats.Length);
        const float scale = 1f / 32768f;
        for (var i = 0; i < n; i++)
            floats[i] = pcm[i] * scale;
    }
}
