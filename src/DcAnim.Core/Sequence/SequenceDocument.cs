namespace DcAnim.Core.Sequence;

/// <summary>
/// Tracks a project through an editing session: mutations, file association
/// and unsaved-change state. All edits funnel through here so the host can
/// reliably prompt before discarding work.
/// </summary>
public sealed class SequenceDocument
{
    private SequenceProject _project = new();
    private bool _isDirty;

    public SequenceProject Project => _project;

    public string? FilePath { get; private set; }

    public bool IsDirty => _isDirty;

    public string DisplayName => (IsDirty ? "*" : string.Empty) + (_project.Name ?? "Untitled");

    public event Action? Changed;

    private void Touch()
    {
        _isDirty = true;
        Changed?.Invoke();
    }

    public void New(string name = "Untitled")
    {
        _project = new SequenceProject { Name = name };
        FilePath = null;
        _isDirty = false;
        Changed?.Invoke();
    }

    public void Load(SequenceProject project, string? path)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        FilePath = path;
        _isDirty = false;
        Changed?.Invoke();
    }

    public void MarkSaved(string path)
    {
        FilePath = path;
        _isDirty = false;
        Changed?.Invoke();
    }

    public SequenceClip AddClip(string animationId, double speed = 1.0, int? index = null)
    {
        if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
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
        Touch();
        return clip;
    }

    public bool RemoveClip(string clipId)
    {
        int i = _project.Clips.FindIndex(c => c.Id == clipId);
        if (i < 0)
            return false;
        _project.Clips.RemoveAt(i);
        Touch();
        return true;
    }

    public SequenceClip DuplicateClip(string clipId)
    {
        var source = _project.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        int i = _project.Clips.IndexOf(source);
        var copy = new SequenceClip
        {
            Id = Guid.NewGuid().ToString("N"),
            AnimationId = source.AnimationId,
            Speed = source.Speed,
        };
        _project.Clips.Insert(i + 1, copy);
        Touch();
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
        var clip = _project.Clips[old];
        _project.Clips.RemoveAt(old);
        _project.Clips.Insert(newIndex, clip);
        Touch();
    }

    public void SetClipSpeed(string clipId, double speed)
    {
        if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
        var clip = _project.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        if (Math.Abs(clip.Speed - speed) < 1e-12)
            return;
        clip.Speed = speed;
        Touch();
    }

    public void SetFps(double fps)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (Math.Abs(_project.Fps - fps) < 1e-12)
            return;
        _project.Fps = fps;
        Touch();
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name must not be empty.", nameof(name));
        if (_project.Name == name)
            return;
        _project.Name = name;
        Touch();
    }
}
