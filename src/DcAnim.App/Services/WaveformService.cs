using NAudio.Wave;

namespace DcAnim.App.Services;

/// <summary>Peak waveform for an audio file: per-bucket max |amplitude|.</summary>
public static class WaveformService
{
    public static float[] GetPeaks(string path, int buckets = 400)
    {
        var peaks = new float[Math.Max(1, buckets)];
        try
        {
            using var reader = new AudioFileReader(path);
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            long totalFrames = reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign);
            if (totalFrames <= 0)
                return peaks;
            var buf = new float[8192 * channels];
            long sample = 0;
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                int frames = read / channels;
                for (int i = 0; i < frames; i++, sample++)
                {
                    float peak = 0;
                    for (int c = 0; c < channels; c++)
                        peak = Math.Max(peak, Math.Abs(buf[i * channels + c]));
                    int b = (int)(sample * buckets / Math.Max(1, totalFrames));
                    if ((uint)b < (uint)peaks.Length && peak > peaks[b])
                        peaks[b] = peak;
                }
            }
        }
        catch
        {
            // Missing/unreadable files yield a flat (empty-looking) waveform.
        }
        return peaks;
    }

    public static double? GetLengthSeconds(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return null;
        }
    }
}
