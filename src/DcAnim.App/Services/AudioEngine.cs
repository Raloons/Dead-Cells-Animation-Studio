using NAudio.Wave;

namespace DcAnim.App.Services;

/// <summary>
/// Windowed WAV playback synchronized to the sequence clock. The engine owns
/// one voice per audible clip and reconciles every tick: voices for clips
/// that are no longer audible are stopped and disposed (no accumulation),
/// continuing clips keep playing, and drift beyond tolerance restarts the
/// voice at the correct offset (covers seeks and loop wraps). All device or
/// file failures degrade to silent skipping plus a one-shot notice.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed record Voice(string ClipId, WaveOutEvent Output, AudioFileReader Reader);

    private readonly List<Voice> _voices = new();
    private readonly HashSet<string> _reportedMissing = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<string>? Problem;
    public event Action? Changed;

    public bool HasVoices
    {
        get { lock (_voices) return _voices.Count > 0; }
    }

    /// <summary>
    /// Reconcile voices with the audible set at sequence time
    /// <paramref name="tSeconds"/>. Call every tick while playing and once
    /// after every seek/pause.
    /// </summary>
    public void Sync(
        IReadOnlyList<DcAnim.Core.Sequence.AudioTimeline.AudibleSpan> audible,
        double tSeconds,
        bool playing)
    {
        if (_disposed)
            return;
        lock (_voices)
        {
            var wanted = playing ? audible.Select(a => a.Clip.Id).ToHashSet() : new HashSet<string>();
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var v = _voices[i];
                var span = audible.FirstOrDefault(a => a.Clip.Id == v.ClipId);
                // Keep only voices that are still wanted AND in sync; anything
                // else (seek, loop wrap, pause, mute) is torn down and rebuilt.
                bool keep = wanted.Contains(v.ClipId) && span is not null &&
                    Math.Abs(CurrentFileTime(v) - span.FileOffsetSeconds) < 0.25;
                if (!keep)
                {
                    try { v.Output.Stop(); } catch { }
                    v.Output.Dispose();
                    v.Reader.Dispose();
                    _voices.RemoveAt(i);
                }
            }
            if (!playing)
            {
                Changed?.Invoke();
                return;
            }
            foreach (var span in audible)
            {
                if (_voices.Any(v => v.ClipId == span.Clip.Id))
                    continue;
                try
                {
                    var reader = new AudioFileReader(span.Clip.AudioPath);
                    reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(span.FileOffsetSeconds,
                        Math.Max(0, reader.TotalTime.TotalSeconds - 0.01)));
                    reader.Volume = (float)Math.Clamp(span.Clip.Volume, 0.0, 1.0);
                    var output = new WaveOutEvent();
                    output.Init(reader);
                    output.Play();
                    _voices.Add(new Voice(span.Clip.Id, output, reader));
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    ReportOnce(span.Clip.AudioPath, $"Audio file missing: {span.Clip.AudioPath}");
                }
                catch (Exception ex)
                {
                    ReportOnce(span.Clip.AudioPath, $"Audio unavailable ({span.Clip.Name}): {ex.Message}");
                }
            }
            Changed?.Invoke();
        }
    }

    public void StopAll()
    {
        lock (_voices)
        {
            foreach (var v in _voices)
            {
                try { v.Output.Stop(); } catch { }
                v.Output.Dispose();
                v.Reader.Dispose();
            }
            _voices.Clear();
            Changed?.Invoke();
        }
    }

    private static double CurrentFileTime(Voice v)
    {
        try
        {
            return v.Reader.CurrentTime.TotalSeconds;
        }
        catch
        {
            return double.NaN; // forces rebuild
        }
    }

    private void ReportOnce(string key, string message)
    {
        if (_reportedMissing.Add(key))
            Problem?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopAll();
    }
}
