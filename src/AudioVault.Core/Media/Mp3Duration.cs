using System.Buffers.Binary;

namespace AudioVault.Core.Media;

/// <summary>
/// Works out an MP3's length from its first kilobytes: exact from a Xing/Info or VBRI header,
/// otherwise from the total size and the constant bitrate.
/// </summary>
public static class Mp3Duration
{
    public const int HeadSize = 64 * 1024;

    private static readonly int[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];

    public static TimeSpan? FromFile(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[Math.Min(HeadSize, file.Length)];
        file.ReadExactly(head);
        return Estimate(head, file.Length);
    }

    /// <param name="totalLength">Size of the whole file, needed for constant-bitrate files.</param>
    public static TimeSpan? Estimate(ReadOnlySpan<byte> head, long? totalLength)
    {
        var start = Id3v2Size(head);
        for (var i = start; i + 4 <= head.Length; i++)
        {
            if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0 || !TryParseHeader(head[i..], out var frame))
            {
                continue;
            }

            // Require a second valid frame where this one says it ends, to skip false syncs in junk data.
            var next = i + frame.Length;
            if (next + 4 <= head.Length && !(head[next] == 0xFF && TryParseHeader(head[next..], out _)))
            {
                continue;
            }

            if (VbrFrameCount(head[i..], frame) is { } frames)
            {
                return TimeSpan.FromSeconds((double)frames * frame.SamplesPerFrame / frame.SampleRate);
            }

            return totalLength is { } total && total > i
                ? TimeSpan.FromSeconds((total - i) * 8.0 / (frame.Bitrate * 1000.0))
                : null;
        }

        return null;
    }

    private static int Id3v2Size(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 || head[0] != 'I' || head[1] != 'D' || head[2] != '3')
        {
            return 0;
        }

        var size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
        var footer = (head[5] & 0x10) != 0 ? 10 : 0;
        return 10 + size + footer;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> b, out Frame frame)
    {
        frame = default;
        if (b.Length < 4)
        {
            return false;
        }

        var version = (b[1] >> 3) & 3; // 0 = MPEG 2.5, 2 = MPEG 2, 3 = MPEG 1
        var layer = (b[1] >> 1) & 3;   // 1 = Layer III, 2 = Layer II
        var bitrateIndex = b[2] >> 4;
        var rateIndex = (b[2] >> 2) & 3;
        if (version == 1 || layer is 0 or 3 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return false;
        }

        var mpeg1 = version == 3;
        var bitrate = layer == 1
            ? (mpeg1 ? Mpeg1Layer3 : Mpeg2Layer3)[bitrateIndex]
            : (mpeg1 ? Mpeg1Layer2 : Mpeg2Layer3)[bitrateIndex];
        var sampleRate = new[] { 44100, 48000, 32000 }[rateIndex] / (version switch { 3 => 1, 2 => 2, _ => 4 });
        var samples = layer == 1 && !mpeg1 ? 576 : 1152;
        var padding = (b[2] >> 1) & 1;
        var length = (samples / 8 * bitrate * 1000 / sampleRate) + padding;
        var mono = (b[3] >> 6) == 3;
        frame = new Frame(bitrate, sampleRate, samples, length, mpeg1, mono, layer == 1);
        return length > 4;
    }

    private static long? VbrFrameCount(ReadOnlySpan<byte> b, Frame frame)
    {
        if (frame.Layer3)
        {
            var sideInfo = frame.Mpeg1 ? (frame.Mono ? 17 : 32) : (frame.Mono ? 9 : 17);
            var xing = 4 + sideInfo;
            if (b.Length >= xing + 12
                && (b.Slice(xing, 4).SequenceEqual("Xing"u8) || b.Slice(xing, 4).SequenceEqual("Info"u8))
                && (BinaryPrimitives.ReadInt32BigEndian(b[(xing + 4)..]) & 1) != 0)
            {
                var frames = BinaryPrimitives.ReadUInt32BigEndian(b[(xing + 8)..]);
                return frames > 0 ? frames : null;
            }
        }

        const int Vbri = 36;
        if (b.Length >= Vbri + 18 && b.Slice(Vbri, 4).SequenceEqual("VBRI"u8))
        {
            var frames = BinaryPrimitives.ReadUInt32BigEndian(b[(Vbri + 14)..]);
            return frames > 0 ? frames : null;
        }

        return null;
    }

    private readonly record struct Frame(int Bitrate, int SampleRate, int SamplesPerFrame, int Length, bool Mpeg1, bool Mono, bool Layer3);
}
