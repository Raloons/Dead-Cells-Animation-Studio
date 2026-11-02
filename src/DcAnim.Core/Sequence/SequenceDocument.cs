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
            return; // first label wins for the grouped step
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
/// saved snapshot - undoing back to the saved state clears the marker).
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

    // ---------- grouped edits (multi-mutation single undo step) ----------

    /// <summary>
    /// Begin a grouped edit (e.g. paste + ripple): mutations until
    /// <see cref="EndGroup"/> collapse into one history step. Must not nest
    /// with an open drag/gesture group.
    /// </summary>
    public void BeginGroup(string label)
    {
        if (History.IsCoalescing)
            throw new InvalidOperationException("Cannot begin an edit group inside an open gesture.");
        BeginDrag(label);
    }

    /// <summary>Commit a grouped edit as a single undo step.</summary>
    public void EndGroup() => EndDrag();

    /// <summary>
    /// End a group, but abandon it (no history entry) when nothing actually
    /// changed - e.g. a no-op split or an empty delete. Returns whether an
    /// undo step was recorded.
    /// </summary>
    public bool EndGroupIfChanged()
    {
        if (_dragSnapshot is null)
        {
            EndGroup();
            return true;
        }
        if (string.Equals(_dragSnapshot, _currentSnapshot, StringComparison.Ordinal))
        {
            AbortGroup();
            return false;
        }
        EndGroup();
        return true;
    }

    /// <summary>Abort a grouped edit, restoring the pre-group state.</summary>
    public void AbortGroup() => AbortDrag();

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
            SourceFirst = source.SourceFirst,
            SourceLast = source.SourceLast,
            Reversed = source.Reversed,
            LeadHoldFrames = source.LeadHoldFrames,
            TailHoldFrames = source.TailHoldFrames,
            BaseTransform = source.BaseTransform,
            Keyframes = source.Keyframes.Select(k => k with { }).ToList(),
            Weapon = CloneWeapon(source.Weapon),
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

    // ---------- splicing: trim, reverse, hold, split, ranges ----------

    /// <summary>Result of <see cref="SplitAt"/>.</summary>
    public sealed record SplitResult(string LeftId, string RightId, int DroppedKeys);

    /// <summary>
    /// Resolve a clip's effective (clamped) trim against available frames.
    /// </summary>
    private static (int First, int Visible) TrimRange(SequenceClip clip, int sourceCount)
    {
        if (sourceCount <= 0)
            return (0, 0);
        int first = Math.Clamp(clip.SourceFirst, 0, sourceCount - 1);
        int last = Math.Clamp(clip.SourceLast ?? (sourceCount - 1), first, sourceCount - 1);
        return (first, last - first + 1);
    }

    private SequenceTiming.SpliceMap MapFor(SequenceClip clip, int sourceCount)
    {
        var (first, visible) = TrimRange(clip, sourceCount);
        int lead = Math.Max(0, clip.LeadHoldFrames);
        int tail = Math.Max(0, clip.TailHoldFrames);
        int baseN = visible > 0
            ? SequenceTiming.ClipOutputFrames(visible, clip.Speed, _project.Fps)
            : 0;
        return new SequenceTiming.SpliceMap(first, visible, clip.Reversed, lead, baseN, tail);
    }

    /// <summary>
    /// Re-anchor keyframes through a structural edit. Keys stick to the
    /// source frame they were authored on: each key is placed at the first
    /// new-local frame showing the same source (last wins on collision).
    /// Keys whose source is no longer visible are dropped (counted by the
    /// caller via list-size comparison). Keys authored in the old hold zone
    /// keep their offset from the end when the new mapping also has a hold
    /// zone, preserving end-pose animation.
    /// </summary>
    private static List<TransformKeyframe> RemapSpan(
        IReadOnlyList<TransformKeyframe> keys,
        SequenceTiming.SpliceMap oldMap, int spanStart, int spanLength,
        SequenceTiming.SpliceMap newMap)
    {
        var result = new List<TransformKeyframe>();
        int oldEnd = spanStart + spanLength;
        int oldTailStart = oldMap.TotalOutputs - oldMap.TailHold;
        int newTailStart = newMap.TotalOutputs - newMap.TailHold;
        foreach (var grp in keys.GroupBy(k => k.Property).OrderBy(g => g.Key))
        {
            foreach (var k in grp.OrderBy(k => k.Frame))
            {
                if (k.Frame < spanStart || k.Frame >= oldEnd)
                    continue;
                int nk;
                if (oldMap.TailHold > 0 && newMap.TailHold > 0 && k.Frame >= oldTailStart)
                {
                    int off = oldMap.TotalOutputs - 1 - k.Frame;
                    nk = newMap.TotalOutputs - 1 - off;
                    if (nk < newTailStart)
                    {
                        // Shrunk hold: fall back to source anchoring.
                        nk = -1;
                    }
                }
                else
                {
                    nk = -1;
                }
                if (nk < 0)
                {
                    int src;
                    try { src = oldMap.SourceAt(k.Frame); }
                    catch (ArgumentOutOfRangeException) { continue; }
                    for (int j = 0; j < newMap.TotalOutputs; j++)
                    {
                        int s;
                        try { s = newMap.SourceAt(j); }
                        catch (ArgumentOutOfRangeException) { break; }
                        if (s == src) { nk = j; break; }
                    }
                    if (nk < 0)
                        continue;
                }
                result.RemoveAll(x => x.Property == k.Property && x.Frame == nk);
                result.Add(k with { Frame = nk });
            }
        }
        result.Sort((a, b) =>
        {
            int c = a.Property.CompareTo(b.Property);
            return c != 0 ? c : a.Frame.CompareTo(b.Frame);
        });
        return result;
    }

    private static List<TransformKeyframe> RemapKeys(
        IReadOnlyList<TransformKeyframe> keys,
        SequenceTiming.SpliceMap oldMap,
        SequenceTiming.SpliceMap newMap) =>
        RemapSpan(keys, oldMap, 0, oldMap.TotalOutputs, newMap);

    private void RequireSources(string animationId, int sourceCount)
    {
        if (sourceCount <= 0)
            throw new InvalidOperationException($"Clip '{animationId}' has no available frames.");
    }

    /// <summary>
    /// Set the visible source range (0-based inclusive). Returns the number
    /// of keyframes dropped because their source left the visible range.
    /// </summary>
    public int SetTrim(string clipId, int first, int? last, int sourceCount)
    {
        var clip = FindClip(clipId);
        if (first < 0)
            throw new ArgumentOutOfRangeException(nameof(first));
        if (last.HasValue && last.Value < first)
            throw new ArgumentOutOfRangeException(nameof(last));
        RequireSources(clip.AnimationId, sourceCount);
        if (first >= sourceCount)
            throw new InvalidOperationException($"Trim start {first} is beyond the {sourceCount} available frames.");
        var oldMap = MapFor(clip, sourceCount);
        Touch($"Trim {clip.AnimationId}");
        clip.SourceFirst = first;
        clip.SourceLast = last;
        var newMap = MapFor(clip, sourceCount);
        var remapped = RemapKeys(clip.Keyframes, oldMap, newMap);
        int dropped = clip.Keyframes.Count - remapped.Count;
        clip.Keyframes = remapped;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return dropped;
    }

    /// <summary>
    /// Set reverse playback. Keyframes mirror with the mapping, so transform
    /// motion reverses in sync. Returns dropped keyframes (normally 0).
    /// </summary>
    public int SetReversed(string clipId, bool reversed, int sourceCount)
    {
        var clip = FindClip(clipId);
        RequireSources(clip.AnimationId, sourceCount);
        if (clip.Reversed == reversed)
            return 0;
        var oldMap = MapFor(clip, sourceCount);
        Touch(reversed ? $"Reverse {clip.AnimationId}" : $"Unreverse {clip.AnimationId}");
        clip.Reversed = reversed;
        var newMap = MapFor(clip, sourceCount);
        var remapped = RemapKeys(clip.Keyframes, oldMap, newMap);
        int dropped = clip.Keyframes.Count - remapped.Count;
        clip.Keyframes = remapped;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return dropped;
    }

    /// <summary>
    /// Set the tail hold (extra repeats of the final displayed frame).
    /// Returns dropped keyframes (keys orphaned by a shrunk hold zone).
    /// </summary>
    public int SetTailHold(string clipId, int holdFrames, int sourceCount)
    {
        if (holdFrames < 0 || holdFrames > SequenceClip.MaxHoldFrames)
            throw new ArgumentOutOfRangeException(nameof(holdFrames));
        var clip = FindClip(clipId);
        RequireSources(clip.AnimationId, sourceCount);
        if (clip.TailHoldFrames == holdFrames)
            return 0;
        var oldMap = MapFor(clip, sourceCount);
        Touch(holdFrames == 0 ? $"Clear hold ({clip.AnimationId})" : $"Hold {holdFrames}f ({clip.AnimationId})");
        clip.TailHoldFrames = holdFrames;
        var newMap = MapFor(clip, sourceCount);
        var remapped = RemapKeys(clip.Keyframes, oldMap, newMap);
        int dropped = clip.Keyframes.Count - remapped.Count;
        clip.Keyframes = remapped;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return dropped;
    }

    /// <summary>
    /// Split the clip covering <paramref name="globalFrame"/> into two clips
    /// at the first source boundary at or after the playhead. Returns null
    /// (no-op) when the frame is outside any clip or at a clip edge.
    /// Halves keep speed/direction; keyframes remap with boundary holds so
    /// evaluated motion stays continuous. The left half keeps the original
    /// id; the right half gets a new one.
    /// </summary>
    public SplitResult? SplitAt(int globalFrame, Func<string, int> sourceCounter)
    {
        var found = LocateSplit(globalFrame, sourceCounter);
        if (found is null)
            return null;
        Touch($"Split {found.Value.Resolved.Clip.AnimationId}");
        var result = SplitResolved(found.Value.Resolved, found.Value.Snap, found.Value.Sources);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return result;
    }

    private (ResolvedClip Resolved, int Snap, int Sources)? LocateSplit(
        int globalFrame, Func<string, int> sourceCounter)
    {
        var resolved = SequenceComposer.Resolve(_project, sourceCounter);
        var r = resolved.FirstOrDefault(r =>
            globalFrame >= r.StartFrame && globalFrame < r.StartFrame + r.OutputFrames);
        if (r is null)
            return null;
        var clip = r.Clip;
        int c = globalFrame - r.StartFrame;
        if (c <= 0)
            return null; // at clip start: nothing to split off
        // Snap forward past any hold run containing the playhead, so both
        // halves cover whole source runs. A cut already on a source
        // boundary stays put; a trailing hold run to the end (or the very
        // last frame) means there is nothing to split off.
        int prevShown = r.Map.SourceAt(c - 1);
        int cSnap = c;
        while (cSnap < r.OutputFrames && r.Map.SourceAt(cSnap) == prevShown)
            cSnap++;
        if (cSnap >= r.OutputFrames)
            return null;
        int sources;
        try { sources = sourceCounter(clip.AnimationId); }
        catch (KeyNotFoundException) { return null; }
        if (sources <= 0)
            return null;
        return (r, cSnap, sources);
    }

    /// <summary>
    /// Delete global output frames <c>[startGlobal, endGlobal)</c>, trimming
    /// or removing overlapped clips. Returns dropped keyframes. The timeline
    /// shrinks by exactly the range length at 1:1 mappings; resampled clips
    /// may drift by a frame per touched clip (documented).
    /// </summary>
    public int DeleteRange(int startGlobal, int endGlobal, Func<string, int> sourceCounter)
    {
        if (endGlobal <= startGlobal)
            return 0;
        var resolved = SequenceComposer.Resolve(_project, sourceCounter);
        if (resolved.Count == 0)
            return 0;
        Touch("Delete frames");
        int droppedTotal = 0;
        for (int i = resolved.Count - 1; i >= 0; i--)
        {
            var r = resolved[i];
            int l0 = Math.Max(startGlobal, r.StartFrame) - r.StartFrame;
            int l1 = Math.Min(endGlobal, r.StartFrame + r.OutputFrames) - r.StartFrame;
            if (l1 <= 0 || l0 >= r.OutputFrames)
                continue;
            l0 = Math.Max(0, l0);
            l1 = Math.Min(r.OutputFrames, l1);
            int idx = _project.Clips.FindIndex(c => c.Id == r.Clip.Id);
            if (idx < 0)
                continue;
            if (l0 == 0 && l1 == r.OutputFrames)
            {
                _project.Clips.RemoveAt(idx);
                continue;
            }
            int sources;
            try { sources = sourceCounter(r.Clip.AnimationId); }
            catch (KeyNotFoundException) { continue; }
            var pieces = new List<(int A, int B)>();
            if (l0 > 0)
                pieces.Add((0, l0));
            if (l1 < r.OutputFrames)
                pieces.Add((l1, r.OutputFrames));
            var replacements = new List<SequenceClip>();
            foreach (var (a, b) in pieces)
            {
                int sMin = int.MaxValue, sMax = int.MinValue;
                for (int k = a; k < b; k++)
                {
                    int s = r.Map.SourceAt(k);
                    sMin = Math.Min(sMin, s);
                    sMax = Math.Max(sMax, s);
                }
                var piece = new SequenceClip
                {
                    Id = Guid.NewGuid().ToString("N"),
                    AnimationId = r.Clip.AnimationId,
                    Speed = r.Clip.Speed,
                    SourceFirst = sMin,
                    SourceLast = sMax,
                    Reversed = r.Clip.Reversed,
                    LeadHoldFrames = a == 0 ? Math.Max(0, r.Clip.LeadHoldFrames) : 0,
                    TailHoldFrames = b == r.OutputFrames ? Math.Max(0, r.Clip.TailHoldFrames) : 0,
                    BaseTransform = r.Clip.BaseTransform,
                    Keyframes = new List<TransformKeyframe>(),
                    Weapon = CloneWeapon(r.Clip.Weapon),
                };
                var pieceMap = MapFor(piece, sources);
                var remapped = RemapSpan(r.Clip.Keyframes, r.Map, a, b - a, pieceMap);
                droppedTotal += r.Clip.Keyframes.Count(k => k.Frame >= a && k.Frame < b) - remapped.Count;
                piece.Keyframes = remapped;
                replacements.Add(piece);
            }
            _project.Clips.RemoveAt(idx);
            _project.Clips.InsertRange(idx, replacements);
        }
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return Math.Max(0, droppedTotal);
    }

    /// <summary>
    /// A non-destructive, self-contained copy of source frames: the unit of
    /// clipboard splice operations.
    /// </summary>
    public sealed record SpliceSegment(
        string AnimationId,
        int SourceFirst,
        int SourceLast,
        double Speed,
        bool Reversed,
        int LeadHoldFrames,
        int TailHoldFrames,
        List<TransformKeyframe> Keyframes)
    {
        public int VisibleSources => SourceLast - SourceFirst + 1;
    }

    /// <summary>
    /// Copy global output frames <c>[startGlobal, endGlobal)</c> as ordered
    /// splice segments (one per overlapped clip, holds included).
    /// </summary>
    public IReadOnlyList<SpliceSegment> CopyRange(
        int startGlobal, int endGlobal, Func<string, int> sourceCounter)
    {
        var segments = new List<SpliceSegment>();
        if (endGlobal <= startGlobal)
            return segments;
        var resolved = SequenceComposer.Resolve(_project, sourceCounter);
        foreach (var r in resolved)
        {
            int l0 = Math.Max(startGlobal, r.StartFrame) - r.StartFrame;
            int l1 = Math.Min(endGlobal, r.StartFrame + r.OutputFrames) - r.StartFrame;
            if (l1 <= 0 || l0 >= r.OutputFrames)
                continue;
            l0 = Math.Max(0, l0);
            l1 = Math.Min(r.OutputFrames, l1);
            int sMin = int.MaxValue, sMax = int.MinValue;
            for (int k = l0; k < l1; k++)
            {
                int s = r.Map.SourceAt(k);
                sMin = Math.Min(sMin, s);
                sMax = Math.Max(sMax, s);
            }
            // Holds overlapped by the span travel with the segment (repeats
            // are identical, so this is exact).
            int lead = 0, tail = 0;
            if (r.LeadHoldFrames > 0)
                lead = Math.Max(0, Math.Min(l1, r.LeadHoldFrames) - l0);
            int tailStart = r.OutputFrames - r.TailHoldFrames;
            if (r.TailHoldFrames > 0)
                tail = Math.Max(0, l1 - Math.Max(l0, tailStart));
            var segMap = new SequenceTiming.SpliceMap(
                sMin, sMax - sMin + 1, r.Clip.Reversed, lead,
                SequenceTiming.ClipOutputFrames(sMax - sMin + 1, r.Clip.Speed, _project.Fps), tail);
            var keys = RemapSpan(r.Clip.Keyframes, r.Map, l0, l1 - l0, segMap);
            segments.Add(new SpliceSegment(
                r.Clip.AnimationId, sMin, sMax, r.Clip.Speed, r.Clip.Reversed,
                lead, tail, keys));
        }
        return segments;
    }

    /// <summary>
    /// Insert splice segments at a global frame (splitting the host clip
    /// when the point falls mid-clip). Returns the created clips in order.
    /// </summary>
    public IReadOnlyList<SequenceClip> PasteSegments(
        int insertGlobal, IReadOnlyList<SpliceSegment> segments, Func<string, int> sourceCounter)
    {
        var created = new List<SequenceClip>();
        if (segments.Count == 0)
            return created;
        foreach (var s in segments)
        {
            if (s.Speed <= 0 || double.IsNaN(s.Speed) || double.IsInfinity(s.Speed))
                throw new ArgumentOutOfRangeException(nameof(segments), "Segment speed must be positive.");
            if (s.SourceFirst < 0 || s.SourceLast < s.SourceFirst)
                throw new ArgumentOutOfRangeException(nameof(segments), "Segment source range is invalid.");
            if (s.LeadHoldFrames < 0 || s.TailHoldFrames < 0 ||
                s.LeadHoldFrames > SequenceClip.MaxHoldFrames || s.TailHoldFrames > SequenceClip.MaxHoldFrames)
                throw new ArgumentOutOfRangeException(nameof(segments), "Segment hold is out of range.");
        }
        Touch($"Paste {segments.Count} segment(s)");
        var resolved = SequenceComposer.Resolve(_project, sourceCounter);
        int total = resolved.Sum(r => r.OutputFrames);
        int at = Math.Clamp(insertGlobal, 0, total);
        var host = resolved.FirstOrDefault(r => at > r.StartFrame && at < r.StartFrame + r.OutputFrames);
        if (host is not null)
        {
            // Split first so the paste lands on a clean boundary. The split
            // shares this Touch (coalesced by the caller group or standalone).
            SplitAtInternal(at, sourceCounter);
            resolved = SequenceComposer.Resolve(_project, sourceCounter);
            total = resolved.Sum(r => r.OutputFrames);
            at = Math.Min(at, total);
        }
        int index = resolved.Count;
        for (int i = 0; i < resolved.Count; i++)
        {
            if (resolved[i].StartFrame >= at) { index = i; break; }
        }
        // Map the resolved index back to a project clip index.
        int projectIndex = _project.Clips.Count;
        if (index < resolved.Count)
        {
            int pi = _project.Clips.FindIndex(c => c.Id == resolved[index].Clip.Id);
            if (pi >= 0)
                projectIndex = pi;
        }
        foreach (var s in segments)
        {
            var clip = new SequenceClip
            {
                Id = Guid.NewGuid().ToString("N"),
                AnimationId = s.AnimationId,
                Speed = s.Speed,
                SourceFirst = s.SourceFirst,
                SourceLast = s.SourceLast,
                Reversed = s.Reversed,
                LeadHoldFrames = s.LeadHoldFrames,
                TailHoldFrames = s.TailHoldFrames,
                Keyframes = s.Keyframes.Select(k => k with { }).ToList(),
            };
            _project.Clips.Insert(projectIndex++, clip);
            created.Add(clip);
        }
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
        return created;
    }

    /// <summary>Split without its own history entry (for grouped callers).</summary>
    private SplitResult? SplitAtInternal(int globalFrame, Func<string, int> sourceCounter)
    {
        var found = LocateSplit(globalFrame, sourceCounter);
        if (found is null)
            return null;
        return SplitResolved(found.Value.Resolved, found.Value.Snap, found.Value.Sources);
    }

    private SplitResult SplitResolved(ResolvedClip r, int cSnap, int sources)
    {
        var clip = r.Clip;
        int leftMin = int.MaxValue, leftMax = int.MinValue;
        for (int k = 0; k < cSnap; k++)
        {
            int s = r.Map.SourceAt(k);
            leftMin = Math.Min(leftMin, s);
            leftMax = Math.Max(leftMax, s);
        }
        int rightMin = int.MaxValue, rightMax = int.MinValue;
        for (int k = cSnap; k < r.OutputFrames; k++)
        {
            int s = r.Map.SourceAt(k);
            rightMin = Math.Min(rightMin, s);
            rightMax = Math.Max(rightMax, s);
        }

        int clipIndex = _project.Clips.IndexOf(clip);
        var left = clip;
        left.SourceFirst = leftMin;
        left.SourceLast = leftMax;
        left.LeadHoldFrames = Math.Max(0, clip.LeadHoldFrames);
        left.TailHoldFrames = 0;
        var leftMap = MapFor(left, sources);

        var right = new SequenceClip
        {
            Id = Guid.NewGuid().ToString("N"),
            AnimationId = clip.AnimationId,
            Speed = clip.Speed,
            SourceFirst = rightMin,
            SourceLast = rightMax,
            Reversed = clip.Reversed,
            LeadHoldFrames = 0,
            TailHoldFrames = Math.Max(0, clip.TailHoldFrames),
            BaseTransform = clip.BaseTransform,
            Keyframes = new List<TransformKeyframe>(),
            Weapon = CloneWeapon(clip.Weapon),
        };
        var rightMap = MapFor(right, sources);

        var leftKeys = RemapSpan(clip.Keyframes, r.Map, 0, cSnap, leftMap);
        var rightKeys = RemapSpan(clip.Keyframes, r.Map, cSnap, r.OutputFrames - cSnap, rightMap);
        int dropped = clip.Keyframes.Count - (leftKeys.Count + rightKeys.Count);

        var props = clip.Keyframes.Select(k => k.Property).Distinct().ToList();
        foreach (var p in props)
        {
            bool hasLeft = clip.Keyframes.Any(k => k.Property == p && k.Frame < cSnap);
            bool hasRight = clip.Keyframes.Any(k => k.Property == p && k.Frame >= cSnap);
            // Boundary keys pin the evaluated motion at the seam. Their value
            // is the original evaluation there; their interpolation is the
            // original previous key's, so a Linear span crossing the cut
            // continues with the identical slope instead of flattening.
            if (hasLeft && hasRight)
            {
                double lv = KeyframeEvaluator.Evaluate(clip.Keyframes, p, cSnap - 1, clip.BaseTransform.Get(p));
                double rv = KeyframeEvaluator.Evaluate(clip.Keyframes, p, cSnap, clip.BaseTransform.Get(p));
                var li = PrevInterpolation(clip.Keyframes, p, cSnap - 1);
                var ri = PrevInterpolation(clip.Keyframes, p, cSnap);
                leftKeys.RemoveAll(k => k.Property == p && k.Frame == leftMap.TotalOutputs - 1);
                leftKeys.Add(new TransformKeyframe(p, leftMap.TotalOutputs - 1, lv, li));
                rightKeys.RemoveAll(k => k.Property == p && k.Frame == 0);
                rightKeys.Add(new TransformKeyframe(p, 0, rv, ri));
            }
            else if (hasLeft)
            {
                double rv = KeyframeEvaluator.Evaluate(clip.Keyframes, p, cSnap, clip.BaseTransform.Get(p));
                var ri = PrevInterpolation(clip.Keyframes, p, cSnap);
                rightKeys.RemoveAll(k => k.Property == p && k.Frame == 0);
                rightKeys.Add(new TransformKeyframe(p, 0, rv, ri));
            }
        }
        SortKeys(leftKeys);
        SortKeys(rightKeys);

        left.Keyframes = leftKeys;
        right.Keyframes = rightKeys;
        _project.Clips.Insert(clipIndex + 1, right);
        return new SplitResult(left.Id, right.Id, Math.Max(0, dropped));
    }

    private static KeyframeInterpolation PrevInterpolation(
        IReadOnlyList<TransformKeyframe> keys, TransformProperty property, int frame)
    {
        TransformKeyframe? prev = null;
        foreach (var k in keys)
        {
            if (k.Property != property || k.Frame > frame)
                continue;
            if (prev is null || k.Frame >= prev.Frame)
                prev = k;
        }
        return prev?.Interpolation ?? KeyframeInterpolation.Hold;
    }

    private static void SortKeys(List<TransformKeyframe> keys) =>
        keys.Sort((a, b) =>
        {
            int c = a.Property.CompareTo(b.Property);
            return c != 0 ? c : a.Frame.CompareTo(b.Frame);
        });

    /// <summary>
    /// Shift effects/audio at or after <paramref name="fromFrame"/> by
    /// <paramref name="deltaFrames"/> (ripple). No-op when delta is 0.
    /// </summary>
    public void ShiftFxAudio(int fromFrame, int deltaFrames)
    {
        if (deltaFrames == 0)
            return;
        Touch("Ripple timeline");
        double fps = _project.Fps;
        foreach (var e in _project.Effects)
        {
            if (e.StartFrame >= fromFrame)
                e.StartFrame = Math.Max(0, e.StartFrame + deltaFrames);
        }
        foreach (var a in _project.Audio)
        {
            if (a.StartSeconds * fps >= fromFrame - 1e-9)
                a.StartSeconds = Math.Max(0, a.StartSeconds + deltaFrames / fps);
        }
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

    // ---------- weapon substitution ----------

    public static WeaponClipOverride? CloneWeapon(WeaponClipOverride? source)
    {
        if (source is null) return null;
        return new WeaponClipOverride
        {
            Enabled = source.Enabled,
            DonorAnimationId = source.DonorAnimationId,
            DonorFrame = source.DonorFrame,
            DonorRegion = source.DonorRegion.Select(r => r with { }).ToList(),
            DonorPivotX = source.DonorPivotX,
            DonorPivotY = source.DonorPivotY,
            BehindBody = source.BehindBody,
            PreserveBody = source.PreserveBody,
            BodyMaskGreen = source.BodyMaskGreen,
            DonorMaskGreen = source.DonorMaskGreen,
            NonBodyMode = source.NonBodyMode,
            BodyColorGreen = source.BodyColorGreen,
            Frames = source.Frames.Select(f => new WeaponFrameOverride
            {
                SourceFrame = f.SourceFrame,
                Remove = f.Remove.Select(r => r with { }).ToList(),
                Foreground = f.Foreground.Select(r => r with { }).ToList(),
                Pose = f.Pose with { },
            }).ToList(),
        };
    }

    public void SetClipWeapon(string clipId, WeaponClipOverride? weapon)
    {
        var clip = FindClip(clipId);
        Touch(weapon is null ? $"Clear weapon ({clip.AnimationId})" : $"Weapon ({clip.AnimationId})");
        clip.Weapon = CloneWeapon(weapon);
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }

    public void SetWeaponPoseDirect(string clipId, int sourceFrame, WeaponPose pose)
    {
        var clip = FindClip(clipId);
        var w = clip.Weapon ?? throw new InvalidOperationException("Clip has no weapon override.");
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Degrees) ||
            !double.IsFinite(pose.ScaleX) || !double.IsFinite(pose.ScaleY) || pose.ScaleX == 0 || pose.ScaleY == 0)
            throw new ArgumentOutOfRangeException(nameof(pose));
        ApplyDirect(() => w.EnsureFrame(sourceFrame).Pose = pose);
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

    public void SetRipple(bool enabled)
    {
        if (_project.RippleFxAudio == enabled)
            return;
        Touch(enabled ? "Ripple on" : "Ripple off");
        _project.RippleFxAudio = enabled;
        _currentSnapshot = Snapshot(_project);
        Changed?.Invoke();
    }
}
