using DcAnim.Core.Model;

namespace DcAnim.Core.Playback;

/// <summary>
/// Deterministic animation playback: no timers, no UI coupling.
/// The host drives <see cref="Tick(TimeSpan)"/> (e.g. from a render loop);
/// all state transitions are explicit and unit-testable.
/// Frame durations come from the clip; <see cref="Speed"/> scales them.
/// </summary>
public sealed class PlaybackEngine
{
    private AnimationClip? _clip;
    private double _positionSeconds; // position within the current loop pass

    public AnimationClip? Clip => _clip;
    public bool IsPlaying { get; private set; }
    public bool Loop { get; set; } = true;

    private double _speed = 1.0;
    public double Speed
    {
        get => _speed;
        set
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Speed must be a positive finite number.");
            _speed = value;
        }
    }

    public int FrameCount => _clip?.FrameCount ?? 0;

    public int CurrentFrameIndex
    {
        get
        {
            if (_clip is null || _clip.FrameCount == 0)
                return -1;
            return FrameIndexAt(_positionSeconds);
        }
    }

    public ClipFrame? CurrentFrame =>
        _clip is null || _clip.FrameCount == 0 ? null : _clip.Frames[CurrentFrameIndex];

    public TimeSpan Position => TimeSpan.FromSeconds(_positionSeconds);

    public TimeSpan Duration => _clip?.GetDuration() ?? TimeSpan.Zero;

    public event Action? FrameChanged;

    public void Load(AnimationClip clip)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        if (clip.FrameCount == 0)
            throw new ArgumentException("Clip has no frames.", nameof(clip));
        _positionSeconds = 0;
        IsPlaying = false;
    }

    public void Unload()
    {
        _clip = null;
        _positionSeconds = 0;
        IsPlaying = false;
    }

    public void Play()
    {
        EnsureClip();
        // Restart from the beginning when pressing play at the very end of a non-looping clip.
        if (!Loop && _positionSeconds >= TotalSeconds())
            _positionSeconds = 0;
        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Restart()
    {
        EnsureClip();
        _positionSeconds = 0;
        FrameChanged?.Invoke();
    }

    public void StepNext()
    {
        EnsureClip();
        int next = CurrentFrameIndex + 1;
        if (next >= _clip!.FrameCount)
            next = Loop ? 0 : _clip.FrameCount - 1;
        SeekFrame(next);
        IsPlaying = false;
    }

    public void StepPrevious()
    {
        EnsureClip();
        int prev = CurrentFrameIndex - 1;
        if (prev < 0)
            prev = Loop ? _clip!.FrameCount - 1 : 0;
        SeekFrame(prev);
        IsPlaying = false;
    }

    public void SeekFrame(int index)
    {
        EnsureClip();
        if (index < 0 || index >= _clip!.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        _positionSeconds = StartOfFrame(index);
        FrameChanged?.Invoke();
    }

    /// <summary>Scrub to a fractional position in [0,1] across the whole clip.</summary>
    public void Scrub(double fraction)
    {
        EnsureClip();
        if (double.IsNaN(fraction))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        _positionSeconds = TotalSeconds() * fraction;
        // Clamp exactly-1.0 to the last frame's interior.
        if (_positionSeconds >= TotalSeconds() && TotalSeconds() > 0)
            _positionSeconds = TotalSeconds() - 1e-6;
        FrameChanged?.Invoke();
    }

    public void Tick(TimeSpan delta)
    {
        if (!IsPlaying || _clip is null || _clip.FrameCount == 0)
            return;
        if (delta <= TimeSpan.Zero)
            return;

        int before = CurrentFrameIndex;
        _positionSeconds += delta.TotalSeconds * _speed;
        double total = TotalSeconds();
        if (_positionSeconds >= total)
        {
            if (Loop && total > 0)
            {
                _positionSeconds %= total;
            }
            else
            {
                _positionSeconds = total - 1e-6; // rest on the last frame
                IsPlaying = false;
            }
        }

        if (CurrentFrameIndex != before)
            FrameChanged?.Invoke();
    }

    private void EnsureClip()
    {
        if (_clip is null)
            throw new InvalidOperationException("No animation clip loaded.");
    }

    private double TotalSeconds()
    {
        double total = 0;
        foreach (var f in _clip!.Frames)
            total += f.Duration.TotalSeconds;
        return total;
    }

    private double StartOfFrame(int index)
    {
        double t = 0;
        for (int i = 0; i < index; i++)
            t += _clip!.Frames[i].Duration.TotalSeconds;
        return t;
    }

    private int FrameIndexAt(double positionSeconds)
    {
        double t = 0;
        for (int i = 0; i < _clip!.Frames.Count; i++)
        {
            t += _clip.Frames[i].Duration.TotalSeconds;
            if (positionSeconds < t)
                return i;
        }
        return _clip.FrameCount - 1;
    }
}
