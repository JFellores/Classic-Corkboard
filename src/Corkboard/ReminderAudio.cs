using System.Media;

namespace Corkboard;

internal sealed record ReminderSoundSettings(string Sound = "Bell", int Volume = 65, bool Repeat = false);

internal sealed class ReminderAudio(string folder) : IDisposable
{
    private SoundPlayer? player;
    private MemoryStream? stream;
    private System.Windows.Forms.Timer? release;

    public static readonly string[] BuiltIn = ["Bell", "Soft chime", "Double chime", "Silent"];

    public string Import(string path)
    {
        byte[] wav = ReadFile(path);
        ReadFormat(wav);
        Directory.CreateDirectory(folder);
        string name = Guid.NewGuid().ToString("N") + ".wav";
        File.WriteAllBytes(Path.Combine(folder, name), wav);
        return name;
    }

    public void Play(ReminderSoundSettings settings, bool loop)
    {
        Stop();
        if (settings.Volume <= 0 || settings.Sound == "Silent") return;
        byte[] wav;
        try
        {
            wav = BuiltIn.Contains(settings.Sound) ? CreateChime(settings.Sound)
                : ReadFile(Path.Combine(folder, Path.GetFileName(settings.Sound)));
            ReadFormat(wav);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            wav = CreateChime("Bell");
        }
        var (start, length, bits, bytesPerSecond) = ReadFormat(wav);
        int volume = Math.Clamp(settings.Volume, 0, 100);
        for (int i = start; i < start + length; i += bits / 8)
        {
            if (bits == 8) wav[i] = (byte)(128 + (wav[i] - 128) * volume / 100);
            else
            {
                short sample = (short)(wav[i] | wav[i + 1] << 8);
                short scaled = (short)(sample * volume / 100);
                wav[i] = (byte)scaled;
                wav[i + 1] = (byte)(scaled >> 8);
            }
        }
        stream = new MemoryStream(wav, writable: false);
        player = new SoundPlayer(stream);
        try
        {
            player.Load();
            if (loop) player.PlayLooping();
            else
            {
                player.Play();
                release = new System.Windows.Forms.Timer
                {
                    Interval = (int)Math.Ceiling(length * 1000.0 / bytesPerSecond) + 500
                };
                release.Tick += (_, _) => Stop();
                release.Start();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Stop();
        }
    }

    public void Stop()
    {
        release?.Dispose();
        release = null;
        player?.Stop();
        player?.Dispose();
        stream?.Dispose();
        player = null;
        stream = null;
    }

    public void Dispose() => Stop();

    private static byte[] ReadFile(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length is < 44 or > 12_000_000) throw new InvalidDataException("Choose a WAV file up to 30 seconds long.");
        var wav = new byte[(int)file.Length];
        file.ReadExactly(wav);
        return wav;
    }

    private static (int Start, int Length, int Bits, int BytesPerSecond) ReadFormat(byte[] wav)
    {
        if (wav.Length >= 44 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            int format = 0, channels = 0, rate = 0, bits = 0;
            for (int chunk = 12; chunk + 8 <= wav.Length;)
            {
                int length = BitConverter.ToInt32(wav, chunk + 4);
                int start = chunk + 8;
                if (length < 0 || length > wav.Length - start) break;
                var tag = wav.AsSpan(chunk, 4);
                if (tag.SequenceEqual("fmt "u8) && length >= 16)
                {
                    format = BitConverter.ToUInt16(wav, start);
                    channels = BitConverter.ToUInt16(wav, start + 2);
                    rate = BitConverter.ToInt32(wav, start + 4);
                    bits = BitConverter.ToUInt16(wav, start + 14);
                }
                else if (tag.SequenceEqual("data"u8) && format == 1 && channels is 1 or 2 && rate is >= 8000 and <= 96000 && bits is 8 or 16)
                {
                    int frame = channels * (bits / 8);
                    if (length > 0 && length % frame == 0 && length <= 30L * rate * frame) return (start, length, bits, rate * frame);
                    break;
                }
                chunk = start + length + (length & 1);
            }
        }
        throw new InvalidDataException("Choose an uncompressed 8- or 16-bit PCM WAV file up to 30 seconds long.");
    }

    private static byte[] CreateChime(string sound)
    {
        const int rate = 24000;
        bool twice = sound == "Double chime";
        double tone = sound == "Soft chime" ? 690 : 1046;
        int count = rate * (twice ? 2 : 1);
        using var output = new MemoryStream(44 + count * 2);
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count * 2);
        for (int i = 0; i < count; i++)
        {
            double time = (double)i / rate;
            double value = Ring(time, tone) + (twice ? Ring(time - 0.30, tone * 1.26) : 0);
            writer.Write((short)Math.Clamp(value * 17000, short.MinValue, short.MaxValue));
        }
        return output.ToArray();

        static double Ring(double time, double pitch)
        {
            if (time is < 0 or > 0.9) return 0;
            double phase = 2 * Math.PI * time * pitch;
            return Math.Min(1, time * 420) * (Math.Sin(phase) * Math.Exp(-6 * time)
                + 0.30 * Math.Sin(phase * 2.41) * Math.Exp(-12 * time)
                + 0.13 * Math.Sin(phase * 3.76) * Math.Exp(-24 * time));
        }
    }
}
