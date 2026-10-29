using System.Text.Json;

namespace DcAnim.Core.Sequence;

/// <summary>
/// Snapshot-based undo/redo: before each mutation the project JSON is pushed.
/// Projects are tiny (metadata only), so full snapshots are cheap, exact, and
/// cover every present and future operation uniformly. Drag operations must
/// bracket with <see cref="BeginCoalesce"/>/<see cref="EndCoalesce"/> so a
/// whole gesture becomes one history step.
/// </summary>
public sealed class UndoHistory
{
    private readonly struct Entry(string Label, string Snapshot)
    {
        public string Label { get; } = Label;
        public string Snapshot { get; } = Snapshot;
    }

    private readonly Stack<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();
    private string? _coalesceLabel;
    private string? _coalesceSnapshot;
    private const int Capacity = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.TryPeek(out var e) ? e.Label : null;
    public string? RedoLabel => _redo.TryPeek(out var e) ? e.Label : null;

    public event Action? Changed;

    internal void Capture(string label, string before)
    {
        if (_coalesceLabel is not null)
        {
            _coalesceLabel = label; // latest label wins for the grouped step
            return;
        }
        Push(new Entry(label, before));
    }

    internal void BeginCoalesce(string label, string before)
    {
        if (_coalesceLabel is null)
            _coalesceSnapshot = before;
        _coalesceLabel = label;
    }

    internal void EndCoalesce()
    {
        if (_coalesceLabel is null)
            return;
        if (_coalesceSnapshot is not null)
            Push(new Entry(_coalesceLabel, _coalesceSnapshot));
        _coalesceLabel = null;
        _coalesceSnapshot = null;
    }

    internal bool IsCoalescing => _coalesceLabel is not null;

    internal void CancelCoalesce()
    {
        _coalesceLabel = null;
        _coalesceSnapshot = null;
    }

    private void Push(Entry entry)
    {
        _undo.Push(entry);
        while (_undo.Count > Capacity)
        {
            // Drop the oldest: rebuild without the bottom entry.
            var kept = _undo.ToArray()[..^1];
            _undo.Clear();
            for (int i = kept.Length - 1; i >= 0; i--)
                _undo.Push(kept[i]);
        }
        _redo.Clear();
        Changed?.Invoke();
    }

    internal string? PopUndo(string current)
    {
        if (!_undo.TryPop(out var entry))
            return null;
        _redo.Push(new Entry(entry.Label, current));
        Changed?.Invoke();
        return entry.Snapshot;
    }

    internal string? PopRedo(string current)
    {
        if (!_redo.TryPop(out var entry))
            return null;
        _undo.Push(new Entry(entry.Label, current));
        Changed?.Invoke();
        return entry.Snapshot;
    }

    internal void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _coalesceLabel = null;
        _coalesceSnapshot = null;
        Changed?.Invoke();
    }
}

/// <summary>
/// Tracks a project through an editing session: all mutations (clips,
/// transforms, keyframes, effects, audio, settings), snapshot undo/redo,
/// file association and exact unsaved-change state (current JSON vs the
/// saved snapshot — undoing back to the saved state clears the marker).
/// </summary>
public sealed class SequenceDocument
{
    private static readonly JsonSerializerOptions SnapshotOptions = new() { WriteIndented = false };

    private SequenceProject _project = new();
    private string _savedSnapshot = Snapshot(new SequenceProject());
    private string _currentSnapshot = Snapshot(new SequenceProject());

    public SequenceProject Project => _project;
    public string? FilePath { get; private set; }
    public UndoHistory History { get; } = new();

    public bool IsDirty =>
        !string.Equals(_currentSnapshot, _savedSnapshot, StringComparison.Ordinal);

    public string DisplayName => (IsDirty ? "*" : string.Empty) + (_project.Name ?? "Untitled");

    public event Action? Changed;

    private void Touch(string undoLabel)
    {
        History.Capture(undoLabel, _currentSnapshot);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    private static string Snapshot(SequenceProject p) =>
        JsonSerializer.Serialize(p, SnapshotOptions);

    private static SequenceProject Restore(string snapshot) =>
        JsonSerializer.Deserialize<SequenceProject>(snapshot, SnapshotOptions)
        ?? throw new InvalidDataException("Undo snapshot is corrupt.");

    public void New(string name = "Untitled")
    {
        _project = new SequenceProject { Name = name };
        FilePath = null;
        History.Clear();
        _currentSnapshot = Snapshot(_project);
        _savedSnapshot = _currentSnapshot;
        Changed?.Invoke();
    }

    public void Load(SequenceProject project, string? path)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        FilePath = path;
        History.Clear();
        _currentSnapshot = Snapshot(_project);
        _savedSnapshot = _currentSnapshot;
        Changed?.Invoke();
    }

    public void MarkSaved(string path)
    {
        FilePath = path;
        _savedSnapshot = _currentSnapshot;
        Changed?.Invoke();
    }

    public bool Undo()
    {
        AbortDrag(); // abandon any in-progress gesture before time-travel
        var snapshot = History.PopUndo(_currentSnapshot);
        if (snapshot is null)
            return false;
        _project = Restore(snapshot);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        AbortDrag(); // abandon any in-progress gesture before time-travel
        var snapshot = History.PopRedo(_currentSnapshot);
        if (snapshot is null)
            return false;
        _project = Restore(snapshot);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    private string? _dragSnapshot;

    /// <summary>Begin a continuous gesture (viewport/keyframe drag): one history step.</summary>
    public void BeginDrag(string label)
    {
        _dragSnapshot = Snapshot(_project);
        History.BeginCoalesce(label, _currentSnapshot);
        Changed?.Invoke();
    }

    /// <summary>Commit a gesture: single undo step + fresh snapshot.</summary>
    public void EndDrag()
    {
        History.EndCoalesce();
        _dragSnapshot = null;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    /// <summary>Abort a gesture, restoring the pre-drag state.</summary>
    public void AbortDrag()
    {
        History.CancelCoalesce();
        if (_dragSnapshot is not null)
        {
            _project = Restore(_dragSnapshot);
            _currentSnapshot = Snapshot(_project);
            _dragSnapshot = null;
            Changed?.Invoke();
        }
    }

    public void CancelDrag() => AbortDrag();

    // ---------- direct (gesture-live) setters: no history capture; the
    // surrounding BeginDrag/EndDrag owns the single undo step ----------

    public void SetClipTransformDirect(string clipId, Transform t) =>
        ApplyDirect(() => FindClip(clipId).BaseTransform = t);

    public void SetEffectTransformDirect(string effectId, Transform t) =>
        ApplyDirect(() => FindEffect(effectId).BaseTransform = t);

    public void MoveEffectDirect(string effectId, int startFrame) =>
        ApplyDirect(() => FindEffect(effectId).StartFrame = Math.Max(0, startFrame));

    public void MoveAudioDirect(string audioId, double startSeconds) =>
        ApplyDirect(() => FindAudio(audioId).StartSeconds = Math.Max(0, startSeconds));

    public void MoveKeyframeDirect(string clipId, TransformProperty property, int fromFrame, int toFrame)
    {
        var clip = FindClip(clipId);
        var key = clip.Keyframes.FirstOrDefault(k => k.Property == property && k.Frame == fromFrame);
        if (key is null)
            return;
        ApplyDirect(() =>
        {
            clip.Keyframes.Remove(key);
            clip.Keyframes.RemoveAll(k => k.Property == property && k.Frame == toFrame);
            clip.Keyframes.Add(key with { Frame = Math.Max(0, toFrame) });
            clip.Keyframes.Sort((a, b) =>
            {
                int c = a.Property.CompareTo(b.Property);
                return c != 0 ? c : a.Frame.CompareTo(b.Frame);
            });
        });
    }

    public void MoveEffectKeyframeDirect(string effectId, TransformProperty property, int fromFrame, int toFrame)
    {
        var e = FindEffect(effectId);
        var key = e.Keyframes.FirstOrDefault(k => k.Property == property && k.Frame == fromFrame);
        if (key is null)
            return;
        ApplyDirect(() =>
        {
            e.Keyframes.Remove(key);
            e.Keyframes.RemoveAll(k => k.Property == property && k.Frame == toFrame);
            e.Keyframes.Add(key with { Frame = Math.Max(0, toFrame) });
            e.Keyframes.Sort((a, b) =>
            {
                int c = a.Property.CompareTo(b.Property);
                return c != 0 ? c : a.Frame.CompareTo(b.Frame);
            });
        });
    }

    private void ApplyDirect(Action mutate)
    {
        mutate();
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    // ---------- clips ----------

    public SequenceClip AddClip(string animationId, double speed = 1.0, int? index = null)
    {
        if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
        Touch($"Add {animationId}");
        var clip = new SequenceClip
        {
            Id = Guid.NewGuid().ToString("N"),
            AnimationId = animationId ?? throw new ArgumentNullException(nameof(animationId)),
            Speed = speed,
        };
        if (index.HasValue)
            _project.Clips.Insert(Math.Clamp(index.Value, 0, _project.Clips.Count), clip);
        else
            _project.Clips.Add(clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return clip;
    }

    public bool RemoveClip(string clipId)
    {
        int i = _project.Clips.FindIndex(c => c.Id == clipId);
        if (i < 0)
            return false;
        Touch($"Remove {DescribeClip(clipId)}");
        _project.Clips.RemoveAt(i);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public SequenceClip DuplicateClip(string clipId)
    {
        var source = _project.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        Touch($"Duplicate {source.AnimationId}");
        int i = _project.Clips.IndexOf(source);
        var copy = new SequenceClip
        {
            Id = Guid.NewGuid().ToString("N"),
            AnimationId = source.AnimationId,
            Speed = source.Speed,
            BaseTransform = source.BaseTransform,
            Keyframes = source.Keyframes.Select(k => k with { }).ToList(),
        };
        _project.Clips.Insert(i + 1, copy);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return copy;
    }

    public void MoveClip(string clipId, int newIndex)
    {
        int old = _project.Clips.FindIndex(c => c.Id == clipId);
        if (old < 0)
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        newIndex = Math.Clamp(newIndex, 0, _project.Clips.Count - 1);
        if (newIndex == old)
            return;
        Touch("Reorder clips");
        var clip = _project.Clips[old];
        _project.Clips.RemoveAt(old);
        _project.Clips.Insert(newIndex, clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetClipSpeed(string clipId, double speed)
    {
        if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
        var clip = _project.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        if (Math.Abs(clip.Speed - speed) < 1e-12)
            return;
        Touch($"Speed {speed:0.##}× ({clip.AnimationId})");
        clip.Speed = speed;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    // ---------- transforms & keyframes ----------

    public void SetClipTransform(string clipId, Transform transform)
    {
        var clip = FindClip(clipId);
        RequireFinite(transform, "transform");
        Touch($"Transform {clip.AnimationId}");
        clip.BaseTransform = transform ?? throw new ArgumentNullException(nameof(transform));
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    private static void RequireFinite(Transform t, string what)
    {
        double[] vals = { t.X, t.Y, t.ScaleX, t.ScaleY, t.RotationDegrees, t.Opacity, t.PivotX, t.PivotY };
        foreach (double v in vals)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentOutOfRangeException(nameof(t), $"{what} must be finite.");
        }
    }

    private static void RequireFiniteKey(TransformKeyframe k, string what)
    {
        if (double.IsNaN(k.Value) || double.IsInfinity(k.Value))
            throw new ArgumentOutOfRangeException(nameof(k), $"{what} value must be finite.");
    }

    public TransformKeyframe AddKeyframe(string clipId, TransformProperty property, int frame, double value,
        KeyframeInterpolation interpolation = KeyframeInterpolation.Hold)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        var clip = FindClip(clipId);
        var key = new TransformKeyframe(property, frame, value, interpolation);
        RequireFiniteKey(key, "keyframe");
        Touch($"Keyframe {property} @ {frame} ({clip.AnimationId})");
        clip.Keyframes.RemoveAll(k => k.Property == property && k.Frame == frame);
        clip.Keyframes.Add(key);
        SortKeys(clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return key;
    }

    public bool RemoveKeyframe(string clipId, TransformProperty property, int frame)
    {
        var clip = FindClip(clipId);
        int removed = clip.Keyframes.RemoveAll(k => k.Property == property && k.Frame == frame);
        if (removed == 0)
            return false;
        Touch($"Delete keyframe ({clip.AnimationId})");
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public bool MoveKeyframe(string clipId, TransformProperty property, int fromFrame, int toFrame)
    {
        if (toFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(toFrame));
        var clip = FindClip(clipId);
        var key = clip.Keyframes.FirstOrDefault(k => k.Property == property && k.Frame == fromFrame);
        if (key is null || fromFrame == toFrame)
            return false;
        Touch($"Move keyframe ({clip.AnimationId})");
        clip.Keyframes.Remove(key);
        clip.Keyframes.RemoveAll(k => k.Property == property && k.Frame == toFrame);
        clip.Keyframes.Add(key with { Frame = toFrame });
        SortKeys(clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public void SetKeyframeValue(string clipId, TransformProperty property, int frame, double value,
        KeyframeInterpolation interpolation)
    {
        // Validate the value before touching the document: invalid input must
        // never reach (or corrupt) stored state, regardless of key existence.
        RequireFiniteKey(new TransformKeyframe(property, frame, value, interpolation), "keyframe");
        var clip = FindClip(clipId);
        var key = clip.Keyframes.FirstOrDefault(k => k.Property == property && k.Frame == frame)
            ?? throw new KeyNotFoundException("Keyframe not found.");
        Touch($"Edit keyframe ({clip.AnimationId})");
        clip.Keyframes.Remove(key);
        clip.Keyframes.Add(key with { Value = value, Interpolation = interpolation });
        SortKeys(clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    private static void SortKeys(SequenceClip clip) =>
        clip.Keyframes.Sort((a, b) =>
        {
            int c = a.Property.CompareTo(b.Property);
            return c != 0 ? c : a.Frame.CompareTo(b.Frame);
        });

    private SequenceClip FindClip(string clipId) =>
        _project.Clips.FirstOrDefault(c => c.Id == clipId)
        ?? throw new KeyNotFoundException($"Clip '{clipId}' not found.");

    private string DescribeClip(string clipId) =>
        _project.Clips.FirstOrDefault(c => c.Id == clipId)?.AnimationId ?? "clip";

    // ---------- effects ----------

    public EffectClip AddEffect(string imagePath, int startFrame = 0, int durationFrames = 30)
    {
        if (durationFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationFrames));
        Touch("Add effect");
        var clip = new EffectClip
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileNameWithoutExtension(imagePath),
            ImagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath)),
            StartFrame = Math.Max(0, startFrame),
            DurationFrames = durationFrames,
            Layer = _project.Effects.Count,
        };
        _project.Effects.Add(clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return clip;
    }

    public bool RemoveEffect(string effectId)
    {
        int i = _project.Effects.FindIndex(e => e.Id == effectId);
        if (i < 0)
            return false;
        Touch("Remove effect");
        _project.Effects.RemoveAt(i);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public void SetEffectImage(string effectId, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must not be empty.", nameof(imagePath));
        var e = FindEffect(effectId);
        if (e.ImagePath == imagePath)
            return;
        Touch("Relink effect image");
        e.ImagePath = imagePath;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void MoveEffect(string effectId, int startFrame)
    {
        var e = FindEffect(effectId);
        startFrame = Math.Max(0, startFrame);
        if (e.StartFrame == startFrame)
            return;
        Touch("Move effect");
        e.StartFrame = startFrame;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public bool MoveEffectKeyframe(string effectId, TransformProperty property, int fromFrame, int toFrame)
    {
        if (toFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(toFrame));
        var e = FindEffect(effectId);
        var key = e.Keyframes.FirstOrDefault(k => k.Property == property && k.Frame == fromFrame);
        if (key is null || fromFrame == toFrame)
            return false;
        Touch("Move effect keyframe");
        e.Keyframes.Remove(key);
        e.Keyframes.RemoveAll(k => k.Property == property && k.Frame == toFrame);
        e.Keyframes.Add(key with { Frame = toFrame });
        e.Keyframes.Sort((a, b) =>
        {
            int c = a.Property.CompareTo(b.Property);
            return c != 0 ? c : a.Frame.CompareTo(b.Frame);
        });
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public void ResizeEffect(string effectId, int durationFrames)
    {
        if (durationFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationFrames));
        var e = FindEffect(effectId);
        if (e.DurationFrames == durationFrames)
            return;
        Touch("Resize effect");
        e.DurationFrames = durationFrames;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetEffectLayer(string effectId, int layer)
    {
        var e = FindEffect(effectId);
        if (e.Layer == layer)
            return;
        Touch("Reorder effect layer");
        e.Layer = layer;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetEffectTransform(string effectId, Transform transform)
    {
        var e = FindEffect(effectId);
        RequireFinite(transform, "transform");
        Touch($"Transform {e.Name}");
        e.BaseTransform = transform ?? throw new ArgumentNullException(nameof(transform));
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public TransformKeyframe AddEffectKeyframe(string effectId, TransformProperty property, int frame, double value,
        KeyframeInterpolation interpolation = KeyframeInterpolation.Hold)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        var e = FindEffect(effectId);
        var key = new TransformKeyframe(property, frame, value, interpolation);
        RequireFiniteKey(key, "effect keyframe");
        Touch($"Effect keyframe {property} @ {frame}");
        e.Keyframes.RemoveAll(k => k.Property == property && k.Frame == frame);
        e.Keyframes.Add(key);
        e.Keyframes.Sort((a, b) =>
        {
            int c = a.Property.CompareTo(b.Property);
            return c != 0 ? c : a.Frame.CompareTo(b.Frame);
        });
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return key;
    }

    public bool RemoveEffectKeyframe(string effectId, TransformProperty property, int frame)
    {
        var e = FindEffect(effectId);
        int removed = e.Keyframes.RemoveAll(k => k.Property == property && k.Frame == frame);
        if (removed == 0)
            return false;
        Touch("Delete effect keyframe");
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    private EffectClip FindEffect(string effectId) =>
        _project.Effects.FirstOrDefault(e => e.Id == effectId)
        ?? throw new KeyNotFoundException($"Effect '{effectId}' not found.");

    // ---------- audio ----------

    public AudioClip AddAudio(string audioPath, double startSeconds = 0)
    {
        if (startSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        Touch("Add audio");
        var clip = new AudioClip
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Path.GetFileNameWithoutExtension(audioPath),
            AudioPath = audioPath ?? throw new ArgumentNullException(nameof(audioPath)),
            StartSeconds = startSeconds,
        };
        _project.Audio.Add(clip);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return clip;
    }

    public bool RemoveAudio(string audioId)
    {
        int i = _project.Audio.FindIndex(a => a.Id == audioId);
        if (i < 0)
            return false;
        Touch("Remove audio");
        _project.Audio.RemoveAt(i);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return true;
    }

    public void MoveAudio(string audioId, double startSeconds)
    {
        if (startSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        var a = FindAudio(audioId);
        if (Math.Abs(a.StartSeconds - startSeconds) < 1e-9)
            return;
        Touch("Move audio");
        a.StartSeconds = startSeconds;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void TrimAudio(string audioId, double trimStartSeconds, double? durationSeconds)
    {
        if (trimStartSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(trimStartSeconds));
        if (durationSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var a = FindAudio(audioId);
        Touch("Trim audio");
        a.TrimStartSeconds = trimStartSeconds;
        a.DurationSeconds = durationSeconds;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetAudioVolume(string audioId, double volume)
    {
        var a = FindAudio(audioId);
        volume = Math.Clamp(volume, 0.0, 1.0);
        if (Math.Abs(a.Volume - volume) < 1e-9)
            return;
        Touch("Audio volume");
        a.Volume = volume;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetAudioMuted(string audioId, bool muted)
    {
        var a = FindAudio(audioId);
        if (a.Muted == muted)
            return;
        Touch(muted ? "Mute audio" : "Unmute audio");
        a.Muted = muted;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetAudioPath(string audioId, string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
            throw new ArgumentException("Audio path must not be empty.", nameof(audioPath));
        var a = FindAudio(audioId);
        if (a.AudioPath == audioPath)
            return;
        Touch("Relink audio");
        a.AudioPath = audioPath;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void RenameEffect(string effectId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));
        var e = FindEffect(effectId);
        if (e.Name == name)
            return;
        Touch("Rename effect");
        e.Name = name;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void RenameAudio(string audioId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));
        var a = FindAudio(audioId);
        if (a.Name == name)
            return;
        Touch("Rename audio");
        a.Name = name;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    private AudioClip FindAudio(string audioId) =>
        _project.Audio.FirstOrDefault(a => a.Id == audioId)
        ?? throw new KeyNotFoundException($"Audio '{audioId}' not found.");

    // ---------- project settings ----------

    public void SetFps(double fps)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (Math.Abs(_project.Fps - fps) < 1e-12)
            return;
        Touch("Sequence FPS");
        _project.Fps = fps;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name must not be empty.", nameof(name));
        if (_project.Name == name)
            return;
        Touch("Rename project");
        _project.Name = name;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetCanvas(int width, int height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (_project.CanvasWidth == width && _project.CanvasHeight == height)
            return;
        Touch("Canvas size");
        _project.CanvasWidth = width;
        _project.CanvasHeight = height;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }
}
