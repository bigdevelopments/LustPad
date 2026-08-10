using System.Buffers.Binary;
using System.Text;

namespace LustPad.Core.Audio;

/// <summary>
/// Writes PCM WAV (16 or 24 bit) with optional sampler (smpl) loop chunk.
/// Sample rate is taken from the audio buffer (48 kHz normal, 96 kHz archival).
/// </summary>
public static class WavWriter
{
    public static void Write(string path, LoopProcessor.LoopResult audio, bool embedLoopPoints, int bitDepth = 16)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(audio);

        bitDepth = bitDepth >= 24 ? 24 : 16;
        int channels = audio.Channels;
        int sampleRate = audio.SampleRate;
        int frames = audio.FrameCount;
        short bitsPerSample = (short)bitDepth;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int bytesPerSample = bitsPerSample / 8;
        int dataSize = frames * blockAlign;

        var pcm = new byte[dataSize];
        if (bitDepth == 16)
        {
            for (int i = 0; i < frames * channels; i++)
            {
                float s = Math.Clamp(audio.Interleaved[i], -1f, 1f);
                short v = (short)Math.Round(s * 32767f);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), v);
            }
        }
        else
        {
            for (int i = 0; i < frames * channels; i++)
            {
                float s = Math.Clamp(audio.Interleaved[i], -1f, 1f);
                int v = (int)Math.Round(s * 8388607f); // 24-bit peak
                v = Math.Clamp(v, -8388608, 8388607);
                int o = i * 3;
                pcm[o] = (byte)(v & 0xFF);
                pcm[o + 1] = (byte)((v >> 8) & 0xFF);
                pcm[o + 2] = (byte)((v >> 16) & 0xFF);
            }
        }

        byte[]? smpl = embedLoopPoints
            ? BuildSmplChunk(audio.LoopStartFrame, audio.LoopEndFrame, sampleRate)
            : null;

        int smplSize = smpl?.Length ?? 0;
        int riffSize = 4 + (8 + 16) + (8 + dataSize) + (smplSize > 0 ? 8 + smplSize : 0);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.ASCII);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        bw.Write(pcm);

        if (smpl is not null)
        {
            bw.Write(Encoding.ASCII.GetBytes("smpl"));
            bw.Write(smpl.Length);
            bw.Write(smpl);
        }
    }

    /// <summary>Reads fmt chunk sample rate and bits-per-sample from a WAV file (for tests).</summary>
    public static (int sampleRate, int bitsPerSample, int channels) ReadFormat(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("Not RIFF");
        br.ReadInt32();
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Not WAVE");

        while (fs.Position + 8 <= fs.Length)
        {
            string id = Encoding.ASCII.GetString(br.ReadBytes(4));
            int size = br.ReadInt32();
            long next = fs.Position + size;
            if (id == "fmt ")
            {
                br.ReadInt16(); // format
                int ch = br.ReadInt16();
                int rate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                int bits = br.ReadInt16();
                return (rate, bits, ch);
            }
            fs.Position = next + (size & 1); // word align
        }

        throw new InvalidDataException("fmt chunk not found");
    }

    private static byte[] BuildSmplChunk(int loopStart, int loopEnd, int sampleRate)
    {
        int inclusiveEnd = Math.Max(loopStart, loopEnd - 1);
        var buf = new byte[60];
        Span<byte> s = buf;

        BinaryPrimitives.WriteUInt32LittleEndian(s[0..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0);
        uint periodNs = (uint)(1_000_000_000.0 / sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], periodNs);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 60);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 0);

        int o = 36;
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 4)..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 8)..], (uint)loopStart);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 12)..], (uint)inclusiveEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 16)..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 20)..], 0);
        return buf;
    }
}
