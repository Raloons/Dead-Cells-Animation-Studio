using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DcAnim.Core.Catalog;
using DcAnim.Core.Export;
using DcAnim.Core.Model;
using DcAnim.Core.Playback;
using DcAnim.Core.Sequence;
using DcAnim.Core.Services;
using DcAnim.Core.Sources;
using DcAnim.App.Controls;
using DcAnim.App.Services;
using DcAnim.App.Views;

namespace DcAnim.App;

public sealed partial class MainWindow : Window
{
    private AnimationLoader? _loader;
    private ThumbnailService? _thumbs;
    private AnimationClip? _clip;
    private readonly PlaybackEngine _engine = new();
    private readonly Dictionary<string, WriteableBitmap> _frameCache = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly WorkspaceSettings _settings = WorkspaceSettings.Load();
    private double _lastTickSeconds;
    private string _preferredAnimation = "AtkKatanaB2";
    private double _fps = 30.0;
    private int _thumbNotified;

    // ---------- sequence editing state ----------
    private enum SeqTargetKind { None, Clip, Effect, Audio }

    private readonly SequenceDocument _doc = new();
    private bool _seqMode;
    private AnimationClip? _seqClip;
    private RenderedSequence? _seqRendered;
    private readonly List<WriteableBitmap> _seqBitmaps = new();
    private IReadOnlyList<ResolvedClip> _seqResolved = Array.Empty<ResolvedClip>();
    private string? _seqSelectedClip;
    private SeqTargetKind _seqTarget = SeqTargetKind.None;
    private string _seqTargetId = string.Empty;
    private TransformProperty? _seqKeyProp;
    private int _seqKeyFrame;
    private bool _seqKeyIsEffect;
    private readonly NativeFrameCache _nativeCache = new();
    private readonly EffectImageCache _fxCache = new();
    private readonly AudioEngine _audio = new();
    private readonly Dictionary<string, float[]> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _dragThrottle = new();
    private bool _closingConfirmed;

    private AnimationClip? ActiveClip => _seqMode ? _seqClip : _clip;
    private int ActiveFrameCount => ActiveClip?.FrameCount ?? 0;

    public MainWindow()
    {
        InitializeComponent();
        ApplySettings();
        ApplyEvidenceSize();

        Browser.AnimationSelected += SelectAnimation;
        Transport.PlayPauseRequested += TogglePlay;
        Transport.FirstRequested += () => { if (CanTransport) { _engine.SeekFrame(0); RefreshFrame(); } };
        Transport.PreviousRequested += () => { if (CanTransport) { _engine.StepPrevious(); RefreshFrame(); } };
        Transport.NextRequested += () => { if (CanTransport) { _engine.StepNext(); RefreshFrame(); } };
        Transport.LastRequested += () =>
        {
            if (ActiveFrameCount > 0) { _engine.SeekFrame(ActiveFrameCount - 1); RefreshFrame(); }
        };
        Transport.SpeedChanged += s => _engine.Speed = s;
        Transport.FpsChanged += ApplyFpsRouted;
        Transport.LoopChanged += loop => _engine.Loop = loop;
        Viewport.FitRequested += FitPreview;
        ViewerModeButton.IsCheckedChanged += (_, _) => { if (ViewerModeButton.IsChecked == true) SetSequenceMode(false); };
        SequenceModeButton.IsCheckedChanged += (_, _) => { if (SequenceModeButton.IsChecked == true) SetSequenceMode(true); };

        Timeline.ScrubRequested += f => { if (CanTransport) { _engine.Scrub(f); RefreshFrame(); } };
        Timeline.SeqClipSelected += OnSeqClipSelected;
        Timeline.SeqClipMoved += (id, i) => { _doc.MoveClip(id, i); AfterStructuralEdit(); };
        Timeline.SeqLibraryDrop += (animId, i) => { var c = _doc.AddClip(animId, 1.0, i); _seqSelectedClip = c.Id; SelectSeqTarget(SeqTargetKind.Clip, c.Id, seekToStart: false); RebuildSequence(); };
        Timeline.SeqRemoveRequested += id => { _doc.RemoveClip(id); AfterStructuralEdit(); };
        Timeline.SeqDuplicateRequested += id => { var c = _doc.DuplicateClip(id); _seqSelectedClip = c.Id; AfterStructuralEdit(); };
        Timeline.SeqSpeedRequested += (id, s) => { _doc.SetClipSpeed(id, s); RebuildSequence(); };
        Timeline.SeqEffectSelected += OnSeqEffectSelected;
        Timeline.SeqEffectMoved += (id, f) => { _doc.MoveEffect(id, f); RebuildSequence(); };
        Timeline.SeqEffectRemoveRequested += id => { _doc.RemoveEffect(id); AfterStructuralEdit(); };
        Timeline.SeqAudioSelected += OnSeqAudioSelected;
        Timeline.SeqAudioMoved += (id, t) => { _doc.MoveAudio(id, t); RebuildSequence(); };
        Timeline.SeqAudioRemoveRequested += id => { _doc.RemoveAudio(id); AfterStructuralEdit(); };
        Timeline.SeqKeyframeSelected += (p, f, fx) => SelectKeyframe(p, f, fx);
        Timeline.SeqKeyframeMoved += (p, a, b, fx) => MoveSelectedKeyframeTo(b);
        Timeline.SeqKeyframeDragStarted += () => _doc.BeginDrag("Move keyframe");
        Timeline.SeqKeyframeDragFinished += () => { _doc.EndDrag(); RebuildSequence(); };
        SeqInspector.ClipSpeedChanged += s => { if (_seqTargetId is { } id && _seqTarget == SeqTargetKind.Clip) { _doc.SetClipSpeed(id, s); RebuildSequence(); } };
        SeqInspector.ProjectFpsChanged += f => { _doc.SetFps(f); RebuildSequence(); };
        SeqInspector.ProjectRenamed += n => { _doc.Rename(n); UpdateTitle(); };
        SeqInspector.ClipTransformCommitted += t => { if (_seqTarget == SeqTargetKind.Clip) { _doc.SetClipTransform(_seqTargetId, t); RebuildSequence(); } };
        SeqInspector.KeyAddRequested += AddKeyframeAtPlayhead;
        SeqInspector.KeyDeleteRequested += (p, f) => DeleteKeyframe(p, f);
        SeqInspector.KeySelected += (p, f) => SelectKeyframe(p, f, _seqTarget == SeqTargetKind.Effect);
        SeqInspector.KeyPrevRequested += () => StepKeyframe(-1);
        SeqInspector.KeyNextRequested += () => StepKeyframe(1);
        SeqInspector.EffectTransformCommitted += t => { if (_seqTarget == SeqTargetKind.Effect) { _doc.SetEffectTransform(_seqTargetId, t); RebuildSequence(); } };
        SeqInspector.EffectStartChanged += v => { if (_seqTarget == SeqTargetKind.Effect) { _doc.MoveEffect(_seqTargetId, v); RebuildSequence(); } };
        SeqInspector.EffectDurationChanged += v => { if (_seqTarget == SeqTargetKind.Effect) { _doc.ResizeEffect(_seqTargetId, v); RebuildSequence(); } };
        SeqInspector.EffectLayerChanged += v => { if (_seqTarget == SeqTargetKind.Effect) { _doc.SetEffectLayer(_seqTargetId, v); RebuildSequence(); } };
        SeqInspector.EffectRenamed += async n => { if (_seqTarget == SeqTargetKind.Effect) { RenameEffect(_seqTargetId, n); } await Task.CompletedTask; };
        SeqInspector.EffectRelinkRequested += async () => await RelinkEffectAsync();
        SeqInspector.EffectRemoveRequested += () => { if (_seqTarget == SeqTargetKind.Effect) { _doc.RemoveEffect(_seqTargetId); AfterStructuralEdit(); } };
        SeqInspector.AudioRenamed += n => { if (_seqTarget == SeqTargetKind.Audio) { RenameAudio(_seqTargetId, n); } };
        SeqInspector.AudioStartChanged += v => { if (_seqTarget == SeqTargetKind.Audio) { _doc.MoveAudio(_seqTargetId, v); RebuildSequence(); } };
        SeqInspector.AudioTrimChanged += v => { if (_seqTarget == SeqTargetKind.Audio) { var a = FindAudio(); if (a is not null) { _doc.TrimAudio(_seqTargetId, v, a.DurationSeconds); RebuildSequence(); } } };
        SeqInspector.AudioVolumeChanged += v => { if (_seqTarget == SeqTargetKind.Audio) { _doc.SetAudioVolume(_seqTargetId, v); } };
        SeqInspector.AudioMutedChanged += m => { if (_seqTarget == SeqTargetKind.Audio) { _doc.SetAudioMuted(_seqTargetId, m); RebuildSequence(); } };
        SeqInspector.AudioRelinkRequested += async () => await RelinkAudioAsync();
        SeqInspector.AudioRemoveRequested += () => { if (_seqTarget == SeqTargetKind.Audio) { _doc.RemoveAudio(_seqTargetId); AfterStructuralEdit(); } };
        Viewport.Preview.MoveStarted += OnViewportDragStart;
        Viewport.Preview.MoveDelta += OnViewportMove;
        Viewport.Preview.MoveFinished += OnViewportDragEnd;
        Viewport.Preview.DoubleTapped += (_, _) => FitPreview();

        OpenPakItem.Click += async (_, _) => await OpenPakAsync();
        OpenAtlasItem.Click += async (_, _) => await OpenAtlasFolderAsync();
        NewProjectItem.Click += async (_, _) => await NewProjectAsync();
        OpenProjectItem.Click += async (_, _) => await OpenProjectAsync();
        SaveItem.Click += async (_, _) => await SaveProjectAsync();
        SaveAsItem.Click += async (_, _) => await SaveProjectAsAsync();
        AddEffectItem.Click += async (_, _) => await AddEffectAsync();
        AddAudioItem.Click += async (_, _) => await AddAudioAsync();
        ExportPngItem.Click += async (_, _) => await ExportAsync(ExportKind.PngSequence);
        ExportGifItem.Click += async (_, _) => await ExportAsync(ExportKind.Gif);
        ExportSheetItem.Click += async (_, _) => await ExportAsync(ExportKind.SpriteSheet);
        ReloadItem.Click += (_, _) => Reload();
        ExitItem.Click += (_, _) => Close();
        PlayPauseItem.Click += (_, _) => TogglePlay();
        UndoItem.Click += (_, _) => Undo();
        RedoItem.Click += (_, _) => Redo();
        PrevFrameItem.Click += (_, _) => { if (CanTransport) { _engine.StepPrevious(); RefreshFrame(); } };
        NextFrameItem.Click += (_, _) => { if (CanTransport) { _engine.StepNext(); RefreshFrame(); } };
        FirstFrameItem.Click += (_, _) => { if (CanTransport) { _engine.SeekFrame(0); RefreshFrame(); } };
        LastFrameItem.Click += (_, _) =>
        {
            if (ActiveFrameCount > 0) { _engine.SeekFrame(ActiveFrameCount - 1); RefreshFrame(); }
        };
        CheckerItem.Click += (_, _) => Viewport.Preview.ShowChecker = CheckerItem.IsChecked;
        FitItem.Click += (_, _) => FitPreview();
        TimelineZoomInItem.Click += (_, _) => Timeline.ZoomIn();
        TimelineZoomOutItem.Click += (_, _) => Timeline.ZoomOut();
        ResetLayoutItem.Click += (_, _) => ResetLayout();
        FormatNotesItem.Click += async (_, _) => await ShowFormatNotesAsync();
        ShortcutsItem.Click += async (_, _) => await ShowShortcutsAsync();
        AboutItem.Click += async (_, _) => await ShowAboutAsync();

        KeyDown += OnKeyDown;
        Closing += async (_, e) =>
        {
            if (IsEvidenceRun() || _closingConfirmed)
            {
                if (!IsEvidenceRun())
                    SaveSettings();
                _audio.Dispose();
                return;
            }
            e.Cancel = true;
            if (await ConfirmDiscardAsync())
            {
                _closingConfirmed = true;
                SaveSettings();
                _audio.Dispose();
                Close();
            }
        };
        _doc.Changed += () => { UpdateTitle(); UpdateUndoMenu(); }; UpdateUndoMenu();
        _audio.Problem += m => Dispatcher.UIThread.Post(() => ShowToast(m, 8));

        _engine.Loop = true;
        _engine.FrameChanged += () => Dispatcher.UIThread.Post(RefreshFrame);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background,
            (_, _) => { if (_toastArmed) HideToast(); });

        Loaded += (_, _) => AutoLoad();
        Opened += (_, _) => _ = MaybeRunEvidenceModeAsync();
    }

    // ---------- source loading ----------

    private void AutoLoad()
    {
        try
        {
            string? resPak = GameSourceLocator.FindResPak();
            if (resPak is not null)
            {
                LoadFromResPak(resPak);
                return;
            }
            // Contextual feedback lives in the library's empty state.
            Browser.Clear();
        }
        catch (Exception ex)
        {
            ShowToast($"Game auto-detect failed: {ex.Message}");
        }
    }

    private IReadOnlyDictionary<string, AnimationTiming>? LoadTimingOverlay()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "data", "timing-overrides.json");
            return File.Exists(path) ? AnimationLoader.LoadTimingOverlay(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private void SetLoader(AnimationLoader loader, string? prefer = null)
    {
        _loader?.Dispose();
        _thumbs?.Dispose();
        _loader = loader;
        _thumbs = new ThumbnailService(loader);
        _thumbs.ThumbnailsChanged += OnThumbnailsChanged;
        _engine.Unload();
        _clip = null;
        ClearCache();
        if (prefer is not null)
            _preferredAnimation = prefer;

        var items = loader.Catalog.Animations
            .Select(a => new AnimationListItem
            {
                Id = a.Id,
                FrameCount = a.FrameCount,
                Category = AnimationCategorizer.Categorize(a.Id),
            })
            .ToList();
        Browser.SetCatalog(items);
        _thumbNotified = 0;
        _thumbs.Prefetch(items.Select(i => i.Id));
        Transport.SetEnabled(false);
        Viewport.SetTitle("No animation", "Select an animation from the library");
        Viewport.SetFrame(null);
        Inspector.Clear();
        Timeline.Clear();
        SourceText.Text = loader.SourceDescription;
        Browser.Select(_preferredAnimation);
    }

    private void OnThumbnailsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_thumbs is null || _loader is null)
                return;
            Browser.ForEachItem(item =>
            {
                if (item.Thumbnail is null)
                    item.Thumbnail = _thumbs.TryGet(item.Id);
            });
            int done = _thumbs.CachedCount;
            if (done != _thumbNotified)
            {
                _thumbNotified = done;
                Browser.SetFooterStatus(done >= _loader.Catalog.Count
                    ? $"{_loader.Catalog.Count} animations"
                    : $"Loading previews… {done} / {_loader.Catalog.Count}");
            }
        });
    }

    private void LoadFromResPak(string resPakPath, string atlasEntry = "atlas/beheaded.atlas")
    {
        try
        {
            SetLoader(AnimationLoader.FromResPak(resPakPath, atlasEntry, LoadTimingOverlay()));
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open '{resPakPath}': {ex.Message}");
        }
    }

    private void LoadFromDirectory(string directory, string atlasFile = "beheaded.atlas")
    {
        try
        {
            SetLoader(AnimationLoader.FromDirectory(directory, atlasFile, LoadTimingOverlay()));
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open '{directory}': {ex.Message}");
        }
    }

    private void Reload()
    {
        if (_loader is null)
        {
            AutoLoad();
            return;
        }
        _preferredAnimation = _clip?.Id ?? _preferredAnimation;
        AutoLoad();
    }

    // ---------- selection & playback ----------

    private void SelectAnimation(string id)
    {
        if (_loader is null)
            return;
        try
        {
            _clip = _loader.LoadClip(id, _fps);
            _engine.Load(_clip);
            _engine.Pause();
            BuildCache();
            Inspector.ShowClip(_clip, _fps);
            Timeline.SetData(_clip.Frames.Select(f => _frameCache[f.RegionName]).ToList<Bitmap>(),
                _clip.Timing?.HitFrame ?? -1, _fps);
            Viewport.SetTitle(_clip.Id,
                $"{_clip.FrameCount} frames · {_clip.Frames[0].Width}×{_clip.Frames[0].Height} px · {_fps:0.#} FPS assumed");
            Viewport.NoteFrameSize(_clip.Frames[0].Width);
            Viewport.ResetUserZoom();
            Viewport.FitToView(_clip.Frames[0].Width, _clip.Frames[0].Height);
            Transport.SetEnabled(true);
            RefreshFrame();
            _preferredAnimation = id;
        }
        catch (Exception ex)
        {
            ShowToast($"Could not load animation '{id}': {ex.Message}");
        }
    }

    private void BuildCache()
    {
        ClearCache();
        if (_clip is null)
            return;
        foreach (var f in _clip.Frames)
            _frameCache[f.RegionName] = FrameBitmap.ToWriteableBitmap(f);
    }

    private void ClearCache()
    {
        foreach (var bmp in _frameCache.Values)
            bmp.Dispose();
        _frameCache.Clear();
    }

    private bool CanTransport => ActiveFrameCount > 0;

    private void TogglePlay()
    {
        if (!CanTransport)
            return;
        if (_engine.IsPlaying)
            _engine.Pause();
        else
            _engine.Play();
        _clock.Restart();
        _lastTickSeconds = 0;
        RefreshFrame();
    }

    private void ApplyFpsRouted(double fps)
    {
        if (fps <= 0)
            return;
        if (_seqMode)
        {
            _doc.SetFps(fps);
            RebuildSequence();
            return;
        }
        ApplyFps(fps);
    }

    private void ApplyFps(double fps)
    {
        if (fps <= 0)
            return;
        _fps = fps;
        if (_clip is null)
            return;
        foreach (var f in _clip.Frames)
            f.Duration = TimeSpan.FromSeconds(1.0 / _fps);
        Inspector.ShowClip(_clip, _fps);
        Timeline.SetFps(_fps);
        Timeline.SetPosition(_engine.CurrentFrameIndex, _clip.FrameCount, _fps);
        Viewport.SetTitle(_clip.Id,
            $"{_clip.FrameCount} frames · {_clip.Frames[0].Width}×{_clip.Frames[0].Height} px · {_fps:0.#} FPS assumed");
        RefreshFrame();
    }

    private void FitPreview()
    {
        var clip = ActiveClip;
        if (clip is null || clip.FrameCount == 0)
            return;
        Viewport.Preview.Pan = new Avalonia.Vector(0, 0);
        Viewport.FitToView(clip.Frames[0].Width, clip.Frames[0].Height);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (ActiveClip is null || !_engine.IsPlaying)
            return;
        if (!_clock.IsRunning)
        {
            _clock.Restart();
            _lastTickSeconds = 0;
            return;
        }
        double now = _clock.Elapsed.TotalSeconds;
        double delta = now - _lastTickSeconds;
        _lastTickSeconds = now;
        _engine.Tick(TimeSpan.FromSeconds(delta));
        // Audio stays glued to the clock even when the frame index is static.
        if (_seqMode)
            SyncAudio();
    }

    private readonly Dictionary<string, double?> _audioLengths = new(StringComparer.OrdinalIgnoreCase);

    private double? AudioLengthOf(string path)
    {
        if (!_audioLengths.TryGetValue(path, out var len))
        {
            len = WaveformService.GetLengthSeconds(path);
            _audioLengths[path] = len;
        }
        return len;
    }

    private void SyncAudio()
    {
        if (!_seqMode || _seqClip is null || _seqClip.FrameCount == 0)
        {
            _audio.StopAll();
            return;
        }
        double t = _engine.Position.TotalSeconds;
        var audible = AudioTimeline.ActiveAt(_doc.Project.Audio, t, AudioLengthOf);
        _audio.Sync(audible, t, _engine.IsPlaying);
    }

    private void RefreshFrame()
    {
        var clip = ActiveClip;
        if (clip is null)
        {
            Viewport.SetFrame(null);
            Transport.SetPlaying(false);
            return;
        }
        var frame = _engine.CurrentFrame;
        if (frame is not null)
        {
            WriteableBitmap? bmp = null;
            if (_seqMode)
            {
                int i = _engine.CurrentFrameIndex;
                if ((uint)i < (uint)_seqBitmaps.Count)
                    bmp = _seqBitmaps[i];
            }
            else if (_frameCache.TryGetValue(frame.RegionName, out var cached))
            {
                bmp = cached;
            }
            if (bmp is not null)
                Viewport.SetFrame(bmp);
        }
        if (_seqMode)
        {
            Timeline.SetSequencePosition(_engine.CurrentFrameIndex, clip.FrameCount);
            UpdateSequenceSubtitle();
            UpdateViewportOverlay();
            SyncAudio();
        }
        else
        {
            Timeline.SetPosition(_engine.CurrentFrameIndex, clip.FrameCount, _fps);
            Inspector.SetPosition(_engine.CurrentFrameIndex, clip.FrameCount, frame?.RegionName ?? "—");
        }
        Transport.SetPlaying(_engine.IsPlaying);
    }

    /// <summary>Editor-only selection overlay (pivot cross + transformed bounds).</summary>
    private void UpdateViewportOverlay()
    {
        Viewport.Preview.MoveEnabled = false;
        Viewport.Preview.SelectionRect = null;
        Viewport.Preview.PivotMarker = null;
        if (!_seqMode || _seqRendered is null)
            return;
        int g = _engine.CurrentFrameIndex;
        if (_seqTarget == SeqTargetKind.Clip)
        {
            var r = _seqResolved.FirstOrDefault(r => g >= r.StartFrame && g < r.StartFrame + r.OutputFrames);
            var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
            if (r is null || clip is null)
                return;
            var native = _nativeCache.Get(_loader!, clip.AnimationId,
                SequenceTiming.SourceIndex(g - r.StartFrame, r.SourceFrames, r.OutputFrames), _doc.Project.Fps);
            var t = clip.EvaluateAt(g - r.StartFrame);
            var (x, y, w, h) = TransformLayout.MapRect(native.Width, native.Height, t,
                _seqRendered.Width, _seqRendered.Height);
            Viewport.Preview.SelectionRect = new Rect(x, y, w, h);
            var (px, py) = TransformLayout.PivotPoint(t, _seqRendered.Width, _seqRendered.Height);
            Viewport.Preview.PivotMarker = new Point(px, py);
            Viewport.Preview.MoveEnabled = true;
        }
        else if (_seqTarget == SeqTargetKind.Effect)
        {
            var e = _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId);
            var bmp = e is null ? null : _fxCache.Get(e.ImagePath);
            if (e is null || bmp is null)
                return;
            int local = Math.Clamp(g - e.StartFrame, 0, Math.Max(0, e.DurationFrames - 1));
            var t = e.EvaluateAt(local);
            var (x, y, w, h) = TransformLayout.MapRect(bmp.Width, bmp.Height, t,
                _seqRendered.Width, _seqRendered.Height);
            Viewport.Preview.SelectionRect = new Rect(x, y, w, h);
            var (px, py) = TransformLayout.PivotPoint(t, _seqRendered.Width, _seqRendered.Height);
            Viewport.Preview.PivotMarker = new Point(px, py);
            Viewport.Preview.MoveEnabled = true;
        }
    }

    private void UpdateSequenceSubtitle()
    {
        if (_seqClip is null || _seqClip.FrameCount == 0)
        {
            Viewport.SetTitle(_doc.Project.Name, "Empty sequence — drag animations from the library");
            return;
        }
        int g = _engine.CurrentFrameIndex;
        var r = _seqResolved.FirstOrDefault(r =>
            g >= r.StartFrame && g < r.StartFrame + r.OutputFrames) ?? _seqResolved.LastOrDefault();
        if (r is null)
            return;
        int local = g - r.StartFrame + 1;
        Viewport.SetTitle(_doc.Project.Name,
            $"Clip {r.ClipIndex + 1}/{_seqResolved.Count} · {r.Clip.AnimationId} " +
            $"frame {local}/{r.OutputFrames} · {r.Clip.Speed:0.##}× · {_seqClip.Frames[0].Width}×{_seqClip.Frames[0].Height} px · {_doc.Project.Fps:0.#} FPS");
    }

    private readonly DispatcherTimer _toastTimer;
    private bool _toastArmed;

    /// <summary>
    /// Contextual error/notice feedback. The persistent status bar is gone;
    /// routine state is visible in the browser and source label, so the toast
    /// is reserved for problems that need attention and auto-dismisses.
    /// </summary>
    private void ShowToast(string message, int seconds = 6)
    {
        ToastText.Text = message;
        Toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 30));
        _toastArmed = true;
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toastArmed = false;
        Toast.IsVisible = false;
    }

    // ---------- menus / dialogs ----------

    private async Task OpenPakAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Dead Cells res.pak",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Dead Cells PAK") { Patterns = new[] { "*.pak" } } },
        });
        if (files.Count > 0)
            LoadFromResPak(files[0].Path.LocalPath);
    }

    private async Task OpenAtlasFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder containing beheaded.atlas + page PNGs",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
            LoadFromDirectory(folders[0].Path.LocalPath);
    }

    private async Task ShowAboutAsync()
    {
        var dialog = new Window
        {
            Title = "About Dead Cells Animation Studio",
            Width = 520,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Text = "Dead Cells Animation Studio (AS-M2)\n\n" +
                       "A standalone workspace for viewing, studying and inspecting Dead Cells " +
                       "animations outside the game.\n\n" +
                       "No game assets are bundled. Point the tool at your own legally " +
                       "obtained Dead Cells installation (res.pak is read directly) or an " +
                       "extracted atlas folder.\n\n" +
                       "Decoders: PAK v1 + BATL (reverse-engineered, validated pixel-exact " +
                       "against the official AtlasTool). Timing overlay: uniform 30 FPS " +
                       "assumption unless a CDB-derived overlay exists.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(16),
            },
        };
        await dialog.ShowDialog(this);
    }

    private async Task ShowShortcutsAsync()
    {
        var rows = new[]
        {
            ("Space", "Play / pause"),
            ("← / →", "Previous / next frame"),
            ("Home / End", "First / last frame"),
            ("Delete", "Remove selected clip / effect / audio"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo (sequence edits)"),
            ("Ctrl+F", "Focus animation search"),
            ("Ctrl+0", "Fit preview to view"),
            ("Ctrl+B", "Toggle checkerboard background"),
            ("Ctrl+N / Ctrl+O", "New / open project"),
            ("Ctrl+S / Ctrl+Shift+S", "Save / save project as"),
            ("Ctrl + wheel", "Zoom timeline (over track)"),
            ("F5", "Reload game source"),
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            RowDefinitions = new RowDefinitions(string.Concat(Enumerable.Repeat("Auto,", rows.Length)).TrimEnd(',')),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            var key = new TextBlock
            {
                Text = rows[i].Item1,
                Foreground = Avalonia.Media.Brushes.White,
                Margin = new Avalonia.Thickness(0, 0, 12, 6),
            };
            var action = new TextBlock
            {
                Text = rows[i].Item2,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xA9, 0xA9, 0xB3)),
                Margin = new Avalonia.Thickness(0, 0, 0, 6),
            };
            grid.Children.Add(key);
            grid.Children.Add(action);
            Grid.SetRow(key, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(key, 0);
            Grid.SetColumn(action, 1);
        }
        var dialog = new Window
        {
            Title = "Keyboard shortcuts",
            Width = 380,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = grid, Margin = new Avalonia.Thickness(16) },
        };
        await dialog.ShowDialog(this);
    }

    private async Task ShowFormatNotesAsync()
    {
        var dialog = new Window
        {
            Title = "Native format notes",
            Width = 560,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text =
                        "res.pak (PAK v1): magic PAK\\x01, table-size hint, hash string, " +
                        "version, root entries. File entry = u8 length + name + u32 " +
                        "offset/size/crc (offsets relative to the data section). Each " +
                        "payload carries a 4-byte header cookie that is stripped on read.\n\n" +
                        "atlas/beheaded.atlas (BATL): magic BATL, then pages " +
                        "(u8 length + page PNG name). Each region = u8 length + name " +
                        "(AnimName_NN), u16 flags, 8×u16 " +
                        "(x, y, w, h, offsetX, offsetY, origW, origH). Regions crop from " +
                        "the page and paste top-left onto the original-size canvas. " +
                        "A 0x00 byte terminates each page.\n\n" +
                        "Known quirk: the shipped beheaded.atlas ends 2 bytes short of a " +
                        "complete final region (scissorsAtkC_04); the official AtlasTool " +
                        "fails there too. The parser drops and counts it.\n\n" +
                        "Frame timing is not stored in native resources: playback assumes " +
                        "uniform FPS (adjustable). Bone tracks in beheaded_tracks.json " +
                        "and CDB weapon rows (animId/hitFrame/charge) are reserved for " +
                        "future overlay layers.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(16),
                },
            },
        };
        await dialog.ShowDialog(this);
    }

    // ---------- keyboard shortcuts ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        bool inText = focused is TextBox;
        bool inMenu = focused is MenuItem || focused is Menu;
        bool onButton = focused is Button;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (inText && e.Key != Key.F)
                return;
            switch (e.Key)
            {
                case Key.F:
                    Browser.FocusSearch();
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    FitPreview();
                    e.Handled = true;
                    break;
                case Key.B:
                    CheckerItem.IsChecked = !CheckerItem.IsChecked;
                    Viewport.Preview.ShowChecker = CheckerItem.IsChecked;
                    e.Handled = true;
                    break;
                case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    _ = OpenProjectAsync();
                    e.Handled = true;
                    break;
                case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    Redo();
                    e.Handled = true;
                    break;
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    break;
                case Key.Y:
                    Redo();
                    e.Handled = true;
                    break;
                case Key.N:
                    _ = NewProjectAsync();
                    e.Handled = true;
                    break;
                case Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    _ = SaveProjectAsAsync();
                    e.Handled = true;
                    break;
                case Key.S:
                    _ = SaveProjectAsync();
                    e.Handled = true;
                    break;
                case Key.Add:
                    Timeline.ZoomIn();
                    e.Handled = true;
                    break;
                case Key.Subtract:
                    Timeline.ZoomOut();
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (inText || inMenu)
            return;

        switch (e.Key)
        {
            case Key.Space when !onButton && CanTransport:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Left when CanTransport:
                _engine.StepPrevious();
                RefreshFrame();
                e.Handled = true;
                break;
            case Key.Right when CanTransport:
                _engine.StepNext();
                RefreshFrame();
                e.Handled = true;
                break;
            case Key.Home when CanTransport:
                _engine.SeekFrame(0);
                RefreshFrame();
                e.Handled = true;
                break;
            case Key.End:
                if (ActiveFrameCount > 0)
                {
                    _engine.SeekFrame(ActiveFrameCount - 1);
                    RefreshFrame();
                    e.Handled = true;
                }
                break;
            case Key.Delete:
                if (_seqMode)
                {
                    if (_seqTarget == SeqTargetKind.Clip && _seqTargetId is { } dc)
                        _doc.RemoveClip(dc);
                    else if (_seqTarget == SeqTargetKind.Effect && _seqTargetId is { } de)
                        _doc.RemoveEffect(de);
                    else if (_seqTarget == SeqTargetKind.Audio && _seqTargetId is { } da)
                        _doc.RemoveAudio(da);
                    else
                        break;
                    AfterStructuralEdit();
                    e.Handled = true;
                }
                break;
        }
    }

    // ---------- workspace persistence ----------

    /// <summary>Evidence mode may request a fixed window size (<c>--size WxH</c>).</summary>
    private void ApplyEvidenceSize()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--size");
        if (i < 0 || i + 1 >= args.Length)
            return;
        var parts = args[i + 1].Split('x');
        if (parts.Length == 2
            && double.TryParse(parts[0], out double w)
            && double.TryParse(parts[1], out double h))
        {
            Width = Math.Clamp(w, MinWidth, 3840);
            Height = Math.Clamp(h, MinHeight, 2160);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void ApplySettings()
    {
        MainGrid.ColumnDefinitions[0].Width = new GridLength(_settings.ClampedLeftWidth);
        MainGrid.ColumnDefinitions[4].Width = new GridLength(_settings.ClampedRightWidth);
        TimelinePanel.Height = _settings.ClampedTimelineHeight;
        CheckerItem.IsChecked = true;
        if (_settings.WindowWidth is { } w && _settings.WindowHeight is { } h)
        {
            Width = Math.Clamp(w, MinWidth, 2560);
            Height = Math.Clamp(h, MinHeight, 1440);
        }
    }

    private void SaveSettings()
    {
        _settings.LeftPanelWidth = MainGrid.ColumnDefinitions[0].Width.Value;
        _settings.RightPanelWidth = MainGrid.ColumnDefinitions[4].Width.Value;
        _settings.TimelineHeight = TimelinePanel.Height;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
    }

    private void ResetLayout()
    {
        _settings.LeftPanelWidth = 300;
        _settings.RightPanelWidth = 300;
        _settings.TimelineHeight = 230;
        _settings.WindowWidth = null;
        _settings.WindowHeight = null;
        _settings.Save();
        ApplySettings();
    }

    // ---------- Viewer / Sequence mode ----------

    private void SetSequenceMode(bool sequence)
    {
        if (_seqMode == sequence)
        {
            SyncModeButtons();
            return;
        }
        if (sequence && _loader is null)
        {
            ShowToast("Open a game source first (File > Open game res.pak…), then build a sequence.");
            SyncModeButtons();
            return;
        }
        _seqMode = sequence;
        SyncModeButtons();
        Inspector.IsVisible = !sequence;
        SeqInspector.IsVisible = sequence;
        SyncTransportFps();
        Timeline.SetSequenceMode(sequence);
        Transport.SetEnabled(sequence ? _seqClip?.FrameCount > 0 : _clip is not null);
        if (sequence)
        {
            RebuildSequence();
            if (_seqClip is not null && _seqClip.FrameCount > 0)
            {
                _engine.Load(_seqClip);
                _engine.Pause();
            }
            else
            {
                _engine.Unload();
            }
        }
        else if (_clip is not null)
        {
            _engine.Load(_clip);
            _engine.Pause();
        }
        else
        {
            _engine.Unload();
        }
        UpdateTitle();
        RefreshFrame();
    }

    private void SyncModeButtons()
    {
        ViewerModeButton.IsChecked = !_seqMode;
        SequenceModeButton.IsChecked = _seqMode;
    }

    private void SyncTransportFps()
    {
        double target = _seqMode ? _doc.Project.Fps : _fps;
        foreach (var item in Transport.FpsItems)
        {
            if (item.Tag?.ToString() is { } tag && double.TryParse(tag, out double v) &&
                Math.Abs(v - target) < 1e-9)
            {
                Transport.FpsBox.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateTitle() =>
        Title = _seqMode
            ? $"{_doc.DisplayName} — Dead Cells Animation Studio"
            : "Dead Cells Animation Studio";

    // ---------- sequence building ----------

    private void ClearSeqCache()
    {
        foreach (var bmp in _seqBitmaps)
            bmp.Dispose();
        _seqBitmaps.Clear();
    }

    /// <summary>
    /// Recomposes the sequence through the shared renderer (transforms +
    /// effect layers) and refreshes every sequence surface. Preview bitmaps,
    /// timeline thumbnails and all exporters consume these exact frames.
    /// </summary>
    private void RebuildSequence(bool selectLastAdded = false)
    {
        if (_loader is null)
            return;
        WarnMissingAssets();

        int keepGlobal = _engine.CurrentFrameIndex;
        ClearSeqCache();
        _seqResolved = SequenceComposer.Resolve(_doc.Project, id => _loader.Catalog.GetFrameRegions(id).Count);
        if (_seqResolved.Count == 0 || _seqResolved.Sum(r => r.OutputFrames) == 0)
        {
            // Empty sequence: nothing to render. Keep a 0-frame clip so the
            // engine, timeline and inspector all see a valid empty state.
            _seqRendered = new RenderedSequence(0, 0, Array.Empty<byte[]>());
            _seqResolved = Array.Empty<ResolvedClip>();
        }
        else
        {
            _seqRendered = SequenceCompositor.Render(_doc.Project, _loader, _nativeCache, _fxCache, out var resolved);
            _seqResolved = resolved;
        }

        var frames = new List<ClipFrame>(_seqRendered.Frames.Count);
        for (int i = 0; i < _seqRendered.Frames.Count; i++)
        {
            frames.Add(new ClipFrame
            {
                Index = i,
                RegionName = $"seq_{i:0000}",
                Width = _seqRendered.Width,
                Height = _seqRendered.Height,
                Rgba = _seqRendered.Frames[i],
                Duration = TimeSpan.FromSeconds(1.0 / _doc.Project.Fps),
            });
            _seqBitmaps.Add(FrameBitmap.FromRgba(_seqRendered.Frames[i], _seqRendered.Width, _seqRendered.Height));
        }
        _seqClip = new AnimationClip
        {
            Id = _doc.Project.Name,
            Frames = frames,
            SourceDescription = $"Sequence '{_doc.Project.Name}' ({_seqResolved.Count} clips, {frames.Count} frames @ {_doc.Project.Fps:0.#} FPS)",
            DefaultFps = _doc.Project.Fps,
        };

        if (_seqSelectedClip is null || _seqResolved.All(r => r.Clip.Id != _seqSelectedClip))
            _seqSelectedClip = _seqResolved.Count > 0 ? _seqResolved[^1].Clip.Id : null;
        if (selectLastAdded && _seqResolved.Count > 0)
            _seqSelectedClip = _seqResolved[^1].Clip.Id;
        if (_seqTarget == SeqTargetKind.Clip && !_doc.Project.Clips.Any(c => c.Id == _seqTargetId))
            ClearSeqTarget();

        RefreshSequenceTimeline(Math.Min(keepGlobal, Math.Max(0, _seqClip.FrameCount - 1)));
        RefreshSequenceInspector();
        Transport.SetEnabled(_seqClip.FrameCount > 0);

        if (_seqMode)
        {
            if (_seqClip.FrameCount > 0)
            {
                bool wasPlaying = _engine.IsPlaying;
                _engine.Load(_seqClip);
                _engine.SeekFrame(Math.Max(0, Math.Min(keepGlobal, _seqClip.FrameCount - 1)));
                if (!wasPlaying)
                    _engine.Pause();
            }
            else
            {
                _engine.Unload();
            }
            UpdateTitle();
            RefreshFrame();
        }
    }

    private void WarnMissingAssets()
    {
        if (_loader is null)
            return;
        var missingAnim = ProjectSerializer.ValidateAnimations(_doc.Project, id => _loader.Catalog.Contains(id));
        var notes = new List<string>();
        if (missingAnim.MissingAnimationIds.Count > 0)
            notes.Add("animations: " + string.Join(", ", missingAnim.MissingAnimationIds.Take(5)));
        var missingFx = _doc.Project.Effects
            .Where(e => !File.Exists(e.ImagePath)).Select(e => e.Name).Distinct().Take(5).ToList();
        if (missingFx.Count > 0)
            notes.Add("effects: " + string.Join(", ", missingFx));
        var missingAu = _doc.Project.Audio
            .Where(a => !File.Exists(a.AudioPath)).Select(a => a.Name).Distinct().Take(5).ToList();
        if (missingAu is { Count: > 0 })
            notes.Add("audio: " + string.Join(", ", missingAu));
        if (notes.Count > 0)
            ShowToast("Missing assets (skipped in preview/exports): " + string.Join("; ", notes));
    }

    private IList<SequenceTrackControl.ClipBlock> BuildBlocks()
    {
        var blocks = new List<SequenceTrackControl.ClipBlock>();
        foreach (var r in _seqResolved)
        {
            var thumbs = new List<Bitmap>();
            if (_seqBitmaps.Count > 0)
            {
                int[] picks = { r.StartFrame, r.StartFrame + r.OutputFrames / 2, r.StartFrame + r.OutputFrames - 1 };
                foreach (int g in picks.Distinct())
                {
                    if ((uint)g < (uint)_seqBitmaps.Count)
                        thumbs.Add(_seqBitmaps[g]);
                }
            }
            blocks.Add(new SequenceTrackControl.ClipBlock(
                r.Clip.Id, r.Clip.AnimationId, r.StartFrame, r.OutputFrames, thumbs));
        }
        return blocks;
    }

    private IList<SequenceTrackControl.FxBlock> BuildFxBlocks() =>
        _doc.Project.Effects
            .OrderBy(e => e.Layer)
            .Select(e => new SequenceTrackControl.FxBlock(e.Id, e.Name, e.StartFrame, e.DurationFrames, e.Layer))
            .ToList();

    private IList<SequenceTrackControl.AudioBlock> BuildAudioBlocks()
    {
        var list = new List<SequenceTrackControl.AudioBlock>();
        foreach (var a in _doc.Project.Audio)
        {
            if (!_waveforms.TryGetValue(a.AudioPath, out var peaks))
            {
                peaks = WaveformService.GetPeaks(a.AudioPath);
                _waveforms[a.AudioPath] = peaks;
            }
            double dur;
            if (a.DurationSeconds is { } fixedDur)
                dur = fixedDur;
            else if (WaveformService.GetLengthSeconds(a.AudioPath) is { } len)
                dur = Math.Max(0, len - a.TrimStartSeconds);
            else
                dur = 60.0;
            dur = Math.Max(0.1, dur);
            list.Add(new SequenceTrackControl.AudioBlock(a.Id, a.Name, a.StartSeconds, dur, peaks, a.Muted));
        }
        return list;
    }

    private IList<SequenceTrackControl.KeyMarker> BuildKeyMarkers()
    {
        IReadOnlyList<TransformKeyframe>? keys = _seqTarget switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId)?.Keyframes,
            SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId)?.Keyframes,
            _ => null,
        };
        if (keys is null)
            return Array.Empty<SequenceTrackControl.KeyMarker>();
        return keys.Select(k => new SequenceTrackControl.KeyMarker(k.Property, k.Frame, false)).ToList();
    }

    private SequenceTrackControl.KeyMarker? BuildSelectedKey() =>
        _seqKeyProp is { } p
            ? new SequenceTrackControl.KeyMarker(p, _seqKeyFrame, true)
            : null;

    private void ClearSeqTarget()
    {
        _seqTarget = SeqTargetKind.None;
        _seqTargetId = string.Empty;
        _seqKeyProp = null;
    }

    private void SelectSeqTarget(SeqTargetKind kind, string id, bool seekToStart = true)
    {
        _seqTarget = kind;
        _seqTargetId = id;
        _seqKeyProp = null;
        if (kind == SeqTargetKind.Clip)
            _seqSelectedClip = id;
        if (seekToStart)
        {
            int start = kind switch
            {
                SeqTargetKind.Clip => _seqResolved.FirstOrDefault(r => r.Clip.Id == id)?.StartFrame ?? 0,
                SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == id)?.StartFrame ?? 0,
                SeqTargetKind.Audio => (int)Math.Round(
                    (_doc.Project.Audio.FirstOrDefault(a => a.Id == id)?.StartSeconds ?? 0) * _doc.Project.Fps),
                _ => _engine.CurrentFrameIndex,
            };
            _engine.SeekFrame(Math.Min(start, Math.Max(0, (_seqClip?.FrameCount ?? 1) - 1)));
        }
        RefreshSequenceTimeline(_engine.CurrentFrameIndex);
        RefreshSequenceInspector();
        RefreshFrame();
    }

    private void OnSeqClipSelected(string clipId) => SelectSeqTarget(SeqTargetKind.Clip, clipId);

    private void OnSeqEffectSelected(string effectId) => SelectSeqTarget(SeqTargetKind.Effect, effectId);

    private void OnSeqAudioSelected(string audioId) => SelectSeqTarget(SeqTargetKind.Audio, audioId, seekToStart: false);

    private void RefreshSequenceTimeline(int currentFrame)
    {
        var blocks = BuildBlocks();
        var fxBlocks = BuildFxBlocks();
        var auBlocks = BuildAudioBlocks();
        Timeline.SetSequenceData(blocks, fxBlocks, auBlocks,
            _seqTarget == SeqTargetKind.Clip ? _seqTargetId : _seqSelectedClip,
            _seqTarget == SeqTargetKind.Effect ? _seqTargetId : null,
            _seqTarget == SeqTargetKind.Audio ? _seqTargetId : null,
            BuildKeyMarkers(), BuildSelectedKey(),
            showEffectKeys: _seqTarget != SeqTargetKind.Clip,
            currentFrame, _doc.Project.Fps);
    }

    private void AfterStructuralEdit()
    {
        if (_seqTarget != SeqTargetKind.None && !SeqTargetExists())
            ClearSeqTarget();
        RebuildSequence();
    }

    private bool SeqTargetExists() => _seqTarget switch
    {
        SeqTargetKind.Clip => _doc.Project.Clips.Any(c => c.Id == _seqTargetId),
        SeqTargetKind.Effect => _doc.Project.Effects.Any(e => e.Id == _seqTargetId),
        SeqTargetKind.Audio => _doc.Project.Audio.Any(a => a.Id == _seqTargetId),
        _ => false,
    };

    private void SelectKeyframe(TransformProperty prop, int frame, bool isEffect)
    {
        if (_seqTarget != SeqTargetKind.Clip && _seqTarget != SeqTargetKind.Effect)
            return;
        bool targetIsEffect = _seqTarget == SeqTargetKind.Effect;
        if (isEffect != targetIsEffect)
            return;
        _seqKeyProp = prop;
        _seqKeyFrame = frame;
        RefreshSequenceTimeline(_engine.CurrentFrameIndex);
        RefreshSequenceInspector();
    }

    private int SeqLocalFrame()
    {
        int anchor = _seqTarget switch
        {
            SeqTargetKind.Clip => _seqResolved.FirstOrDefault(r => r.Clip.Id == _seqTargetId)?.StartFrame ?? 0,
            SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId)?.StartFrame ?? 0,
            _ => 0,
        };
        return Math.Max(0, _engine.CurrentFrameIndex - anchor);
    }

    private void AddKeyframeAtPlayhead(TransformProperty prop)
    {
        if (_seqTarget != SeqTargetKind.Clip && _seqTarget != SeqTargetKind.Effect)
            return;
        int local = SeqLocalFrame();
        double value = EvaluateCurrent(prop);
        if (_seqTarget == SeqTargetKind.Clip)
            _doc.AddKeyframe(_seqTargetId, prop, local, value);
        else
            _doc.AddEffectKeyframe(_seqTargetId, prop, local, value);
        _seqKeyProp = prop;
        _seqKeyFrame = local;
        RebuildSequence();
    }

    private double EvaluateCurrent(TransformProperty prop)
    {
        int local = SeqLocalFrame();
        if (_seqTarget == SeqTargetKind.Clip)
        {
            var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
            if (clip is null)
                return Transform.Identity.Get(prop);
            return KeyframeEvaluator.Evaluate(clip.Keyframes, prop, local, clip.BaseTransform.Get(prop));
        }
        var e = _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId);
        if (e is null)
            return Transform.Identity.Get(prop);
        return KeyframeEvaluator.Evaluate(e.Keyframes, prop, local, e.BaseTransform.Get(prop));
    }

    private void DeleteKeyframe(TransformProperty prop, int frame)
    {
        if (_seqTarget == SeqTargetKind.Clip)
            _doc.RemoveKeyframe(_seqTargetId, prop, frame);
        else if (_seqTarget == SeqTargetKind.Effect)
            _doc.RemoveEffectKeyframe(_seqTargetId, prop, frame);
        else
            return;
        if (_seqKeyProp == prop && _seqKeyFrame == frame)
            _seqKeyProp = null;
        RebuildSequence();
    }

    private void StepKeyframe(int direction)
    {
        IReadOnlyList<TransformKeyframe>? keys = _seqTarget switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId)?.Keyframes,
            SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId)?.Keyframes,
            _ => null,
        };
        if (keys is null || keys.Count == 0)
            return;
        int anchor = _seqTarget == SeqTargetKind.Clip
            ? _seqResolved.FirstOrDefault(r => r.Clip.Id == _seqTargetId)?.StartFrame ?? 0
            : _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId)?.StartFrame ?? 0;
        var ordered = keys.Select(k => k.Frame).Distinct().OrderBy(f => f).ToList();
        int local = SeqLocalFrame();
        int target = direction > 0
            ? ordered.FirstOrDefault(f => f > local, -1)
            : ordered.LastOrDefault(f => f < local, -1);
        if (target < 0)
            target = direction > 0 ? ordered[0] : ordered[^1];
        var key = keys.First(k => k.Frame == target);
        _seqKeyProp = key.Property;
        _seqKeyFrame = target;
        _engine.SeekFrame(Math.Min(anchor + target, Math.Max(0, (_seqClip?.FrameCount ?? 1) - 1)));
        RefreshSequenceTimeline(_engine.CurrentFrameIndex);
        RefreshSequenceInspector();
        RefreshFrame();
    }

    private void MoveSelectedKeyframeTo(int toFrame)
    {
        if (_seqKeyProp is not { } prop)
            return;
        if (_seqTarget == SeqTargetKind.Clip)
            _doc.MoveKeyframeDirect(_seqTargetId, prop, _seqKeyFrame, Math.Max(0, toFrame));
        else if (_seqTarget == SeqTargetKind.Effect)
            _doc.MoveEffectKeyframeDirect(_seqTargetId, prop, _seqKeyFrame, Math.Max(0, toFrame));
        else
            return;
        _seqKeyFrame = Math.Max(0, toFrame);
        RebuildSequence();
    }

    private void RenameEffect(string id, string name)
    {
        try
        {
            _doc.RenameEffect(id, name);
            RebuildSequence();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
    }

    private async Task RelinkEffectAsync()
    {
        if (_seqTarget != SeqTargetKind.Effect)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose replacement effect image",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png" } } },
        });
        if (files.Count == 0)
            return;
        _doc.SetEffectImage(_seqTargetId, files[0].Path.LocalPath);
        RebuildSequence();
    }

    private async Task RelinkAudioAsync()
    {
        if (_seqTarget != SeqTargetKind.Audio)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose replacement WAV file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } },
        });
        if (files.Count == 0)
            return;
        _doc.SetAudioPath(_seqTargetId, files[0].Path.LocalPath);
        _waveforms.Remove(files[0].Path.LocalPath);
        _audioLengths.Remove(files[0].Path.LocalPath);
        RebuildSequence();
    }

    private async Task AddEffectAsync()
    {
        if (_loader is null)
        {
            ShowToast("Open a game source first.");
            return;
        }
        if (!_seqMode)
            SetSequenceMode(true);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import transparent PNG effect",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png" } } },
        });
        if (files.Count == 0)
            return;
        try
        {
            int start = _seqClip?.FrameCount ?? 0;
            var fx = _doc.AddEffect(files[0].Path.LocalPath, Math.Max(0, _engine.CurrentFrameIndex));
            _ = start;
            SelectSeqTarget(SeqTargetKind.Effect, fx.Id, seekToStart: false);
            RebuildSequence();
        }
        catch (Exception ex)
        {
            ShowToast($"Could not add effect: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    private async Task AddAudioAsync()
    {
        if (_loader is null)
        {
            ShowToast("Open a game source first.");
            return;
        }
        if (!_seqMode)
            SetSequenceMode(true);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import WAV audio",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } },
        });
        if (files.Count == 0)
            return;
        try
        {
            double t = _seqClip is null || _seqClip.FrameCount == 0
                ? 0
                : _engine.CurrentFrameIndex / _doc.Project.Fps;
            var au = _doc.AddAudio(files[0].Path.LocalPath, Math.Max(0, t));
            SelectSeqTarget(SeqTargetKind.Audio, au.Id, seekToStart: false);
            RebuildSequence();
        }
        catch (Exception ex)
        {
            ShowToast($"Could not add audio: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    private void RenameAudio(string id, string name)
    {
        try
        {
            _doc.RenameAudio(id, name);
            RebuildSequence();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
    }

    private AudioClip? FindAudio() =>
        _doc.Project.Audio.FirstOrDefault(a => a.Id == _seqTargetId);

    private void Undo()
    {
        if (!_seqMode || !_doc.History.CanUndo)
            return;
        if (_doc.Undo())
        {
            if (!_seqResolved.Any(r => r.Clip.Id == _seqSelectedClip))
                _seqSelectedClip = _doc.Project.Clips.FirstOrDefault()?.Id;
            if (_seqTarget != SeqTargetKind.None && !SeqTargetExists())
                ClearSeqTarget();
            RebuildSequence();
            UpdateUndoMenu();
        }
    }

    private void Redo()
    {
        if (!_seqMode || !_doc.History.CanRedo)
            return;
        if (_doc.Redo())
        {
            if (!_seqResolved.Any(r => r.Clip.Id == _seqSelectedClip))
                _seqSelectedClip = _doc.Project.Clips.FirstOrDefault()?.Id;
            if (_seqTarget != SeqTargetKind.None && !SeqTargetExists())
                ClearSeqTarget();
            RebuildSequence();
            UpdateUndoMenu();
        }
    }

    private void UpdateUndoMenu()
    {
        UndoItem.Header = _doc.History.UndoLabel is { } u ? $"Undo {u}" : "Undo";
        UndoItem.IsEnabled = _seqMode && _doc.History.CanUndo;
        RedoItem.Header = _doc.History.RedoLabel is { } r ? $"Redo {r}" : "Redo";
        RedoItem.IsEnabled = _seqMode && _doc.History.CanRedo;
    }

    // ---------- viewport direct manipulation ----------

    private Transform? _moveBase;
    private readonly Stopwatch _moveThrottle = new();

    private void OnViewportDragStart()
    {
        if (!_seqMode)
            return;
        _moveBase = _seqTarget switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId)?.BaseTransform,
            SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId)?.BaseTransform,
            _ => null,
        };
        if (_moveBase is null)
            return;
        _moveThrottle.Restart();
        _doc.BeginDrag("Move selection");
    }

    private void OnViewportMove(Vector total)
    {
        if (_moveBase is null || !_seqMode)
            return;
        if (_moveThrottle.IsRunning && _moveThrottle.ElapsedMilliseconds < 40)
            return;
        _moveThrottle.Restart();
        double zoom = Math.Max(1.0, Viewport.CurrentZoom);
        // Absolute position from gesture start: no drift, no accumulation error.
        if (_seqTarget == SeqTargetKind.Clip)
        {
            _doc.SetClipTransformDirect(_seqTargetId,
                _moveBase with { X = _moveBase.X + total.X / zoom, Y = _moveBase.Y + total.Y / zoom });
        }
        else if (_seqTarget == SeqTargetKind.Effect)
        {
            _doc.SetEffectTransformDirect(_seqTargetId,
                _moveBase with { X = _moveBase.X + total.X / zoom, Y = _moveBase.Y + total.Y / zoom });
        }
        else
        {
            return;
        }
        RebuildSequence();
        RefreshFrame();
    }

    private void OnViewportDragEnd()
    {
        if (_moveBase is null)
            return;
        _moveBase = null;
        _doc.EndDrag();
        RebuildSequence();
        RefreshFrame();
    }

    private void UpdateSeqInspector() => RefreshSequenceInspector();

    private void RefreshSequenceInspector()
    {
        switch (_seqTarget)
        {
            case SeqTargetKind.Clip:
            {
                var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
                var r = _seqResolved.FirstOrDefault(r => r.Clip.Id == _seqTargetId);
                if (clip is null || r is null)
                {
                    SeqInspector.ShowNothing();
                    break;
                }
                SeqInspector.ShowClip(clip.AnimationId, clip.Speed, r.OutputFrames, _doc.Project.Fps,
                    clip.BaseTransform, clip.Keyframes);
                if (_seqKeyProp is { } prop)
                {
                    var key = clip.Keyframes.FirstOrDefault(k => k.Property == prop && k.Frame == _seqKeyFrame);
                    SeqInspector.ShowKeys(clip.Keyframes);
                    SeqInspector.HighlightKey(prop, key?.Frame ?? _seqKeyFrame);
                }
                else
                {
                    SeqInspector.ShowKeys(clip.Keyframes);
                }
                break;
            }
            case SeqTargetKind.Effect:
            {
                var e = _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId);
                if (e is null)
                {
                    SeqInspector.ShowNothing();
                    break;
                }
                SeqInspector.ShowEffect(e.Name, e.ImagePath, e.StartFrame, e.DurationFrames, e.Layer, e.BaseTransform);
                SeqInspector.ShowKeys(e.Keyframes);
                break;
            }
            case SeqTargetKind.Audio:
            {
                var a = _doc.Project.Audio.FirstOrDefault(a => a.Id == _seqTargetId);
                if (a is null)
                {
                    SeqInspector.ShowNothing();
                    break;
                }
                SeqInspector.ShowAudio(a.Name, a.AudioPath, a.StartSeconds, a.TrimStartSeconds,
                    a.DurationSeconds ?? WaveformService.GetLengthSeconds(a.AudioPath) ?? 0, a.Volume, a.Muted);
                break;
            }
            default:
            {
                var r = _seqResolved.FirstOrDefault(rr => rr.Clip.Id == _seqSelectedClip);
                if (r is not null)
                {
                    var clip = _doc.Project.Clips.First(c => c.Id == r.Clip.Id);
                    SeqInspector.ShowClip(clip.AnimationId, clip.Speed, r.OutputFrames, _doc.Project.Fps,
                        clip.BaseTransform, clip.Keyframes);
                }
                else
                {
                    SeqInspector.ShowNothing();
                }
                break;
            }
        }
        SeqInspector.ShowProject(_doc.Project.Name, _doc.Project.Fps,
            _seqResolved.Count, _seqClip?.FrameCount ?? 0);
    }

    // ---------- project persistence ----------

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_doc.IsDirty)
            return true;
        var dialog = new Window
        {
            Title = "Unsaved changes",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var tcs = new TaskCompletionSource<bool>();
        var save = new Button { Content = "Save", MinWidth = 90 };
        var noSave = new Button { Content = "Don't Save", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        save.Click += async (_, _) => { dialog.Close(); tcs.TrySetResult(await SaveProjectAsync()); };
        noSave.Click += (_, _) => { dialog.Close(); tcs.TrySetResult(true); };
        cancel.Click += (_, _) => { dialog.Close(); tcs.TrySetResult(false); };
        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = $"Project '{_doc.Project.Name}' has unsaved changes. Save before continuing?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { save, noSave, cancel },
                },
            },
        };
        _ = dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task NewProjectAsync()
    {
        if (_loader is null)
        {
            ShowToast("Open a game source first (File > Open game res.pak…), then start a project.");
            return;
        }
        if (!await ConfirmDiscardAsync())
            return;
        _doc.New();
        _seqSelectedClip = null;
        SetSequenceMode(true);
        RebuildSequence();
    }

    private async Task OpenProjectAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return;
        if (!await ConfirmDiscardAsync())
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open animation project",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Animation Studio project") { Patterns = new[] { "*.dcanim" } } },
        });
        if (files.Count == 0)
            return;
        await OpenProjectFileAsync(files[0].Path.LocalPath);
    }

    private async Task OpenProjectFileAsync(string path)
    {
        try
        {
            var project = ProjectSerializer.Load(path);
            if (_loader is null)
            {
                ShowToast("Open a game source first — the project only stores references, not artwork.");
                return;
            }
            var missing = ProjectSerializer.ValidateAnimations(project, id => _loader.Catalog.Contains(id));
            _doc.Load(project, path);
            _seqSelectedClip = null;
            SetSequenceMode(true);
            RebuildSequence();
            if (missing.MissingAnimationIds.Count > 0)
                ShowToast("Project references missing animations (check game version): " +
                    string.Join(", ", missing.MissingAnimationIds.Take(5)));
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open project: {ex.Message}");
            await Task.CompletedTask;
        }
    }

    private async Task<bool> SaveProjectAsync()
    {
        if (_doc.FilePath is null)
            return await SaveProjectAsAsync();
        try
        {
            ProjectSerializer.Save(_doc.Project, _doc.FilePath);
            _doc.MarkSaved(_doc.FilePath);
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"Could not save project: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SaveProjectAsAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
            return false;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save animation project",
            SuggestedFileName = $"{_doc.Project.Name}.dcanim",
            FileTypeChoices = new[] { new FilePickerFileType("Animation Studio project") { Patterns = new[] { "*.dcanim" } } },
        });
        if (file is null)
            return false;
        try
        {
            ProjectSerializer.Save(_doc.Project, file.Path.LocalPath);
            _doc.MarkSaved(file.Path.LocalPath);
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"Could not save project: {ex.Message}");
            return false;
        }
    }

    // ---------- export ----------

    private async Task ExportAsync(ExportKind kind)
    {
        if (_loader is null)
        {
            ShowToast("Open a game source first.");
            return;
        }
        if (_seqClip is null || _seqClip.FrameCount == 0)
        {
            // Build on demand so export reflects the current project.
            RebuildSequence();
        }
        if (_seqClip is null || _seqClip.FrameCount == 0 || _seqRendered is null)
        {
            ShowToast("Add clips to the sequence first (drag animations from the library).");
            return;
        }
        // Preview and exports share these exact composed frames.
        var rendered = new RenderedSequence(
            _seqRendered.Width, _seqRendered.Height,
            _seqClip.Frames.Select(f => f.Rgba).ToList());

        var dialog = new ExportDialog(kind, _seqClip.FrameCount, _doc.Project.Fps);
        _ = dialog.ShowDialog(this);
        var settings = await dialog.WaitForSettingsAsync();
        if (settings is null)
            return;

        var progress = new Progress<ExportProgress>(p =>
            Dispatcher.UIThread.Post(() => dialog.ReportProgress(p.Fraction, p.Message)));
        try
        {
            switch (settings.Kind)
            {
                case ExportKind.PngSequence:
                    await Task.Run(() => PngSequenceExporter.ExportRendered(
                        rendered, settings.Destination, settings.Overwrite, progress));
                    dialog.ReportDone($"Exported {_seqClip.FrameCount} PNG frames to {settings.Destination}");
                    break;
                case ExportKind.Gif:
                    GifExporter.GifExportInfo info = default!;
                    await Task.Run(() => info = GifExporter.ExportRendered(
                        rendered, _doc.Project.Fps, settings.Destination,
                        settings.GifLoop, settings.Overwrite, progress));
                    dialog.ReportDone(
                        $"Exported GIF ({info.FrameCount} frames, {info.EffectiveFps:0.#} FPS effective, " +
                        $"loop {(info.Loop ? "on" : "off")}) to {info.Path}");
                    break;
                case ExportKind.SpriteSheet:
                    SpriteSheetExporter.SheetInfo sheet = default!;
                    string meta = System.IO.Path.ChangeExtension(settings.Destination, ".json");
                    await Task.Run(() => sheet = SpriteSheetExporter.Export(
                        rendered, _seqResolved, _doc.Project.Fps,
                        settings.Destination, meta, settings.SheetColumns,
                        settings.SheetPadding, settings.Overwrite, progress));
                    dialog.ReportDone(
                        $"Exported {sheet.ImageWidth}×{sheet.ImageHeight} sheet + metadata to {System.IO.Path.GetDirectoryName(settings.Destination)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            dialog.ReportError($"Export failed: {ex.Message}");
        }
    }

    // ---------- evidence mode ----------

    private static bool IsEvidenceRun() =>
        Array.IndexOf(Environment.GetCommandLineArgs(), "--evidence") >= 0;

    /// <summary>
    /// Builds the Katana evidence sequence with AS-M4 authoring (clip
    /// keyframes, an effect overlay, an audio track, undo/redo), then saves,
    /// reopens and exports. Outputs are verified externally (docs/AS-M4).
    /// Effect art and test audio are generated locally — no game/mod assets.
    /// </summary>
    private async Task RunSequenceEvidenceAsync(string dir)
    {
        GenerateEvidenceAssets(dir, out string fxPath, out string wavPath);

        // Empty-project path first: this must never crash (New Project with
        // zero clips exercises the empty-sequence guards end to end).
        _doc.New("Empty check");
        SetSequenceMode(true);
        TogglePlay();
        RefreshFrame();
        await Task.Delay(300);

        _doc.New("Katana Combo");
        _doc.AddClip("AtkKatanaA", 1.0);
        _doc.AddClip("AtkKatanaB2", 1.0);
        _doc.AddClip("AtkKatanaB3", 0.5);
        _doc.AddClip("AtkKatanaB4", 1.25);
        // Transform keyframes on the second clip (B2): lunge out and scale pulse.
        var b2 = _doc.Project.Clips[1];
        _doc.AddKeyframe(b2.Id, TransformProperty.X, 0, 0);
        _doc.AddKeyframe(b2.Id, TransformProperty.X, 5, 12);
        _doc.AddKeyframe(b2.Id, TransformProperty.ScaleX, 0, 1);
        _doc.AddKeyframe(b2.Id, TransformProperty.ScaleX, 5, 1.5, KeyframeInterpolation.Linear);
        _doc.AddKeyframe(b2.Id, TransformProperty.ScaleX, 10, 1, KeyframeInterpolation.Linear);
        // Effect overlay at the B2 strike: expanding fading ring.
        var fx = _doc.AddEffect(fxPath, startFrame: 26, durationFrames: 12);
        _doc.AddEffectKeyframe(fx.Id, TransformProperty.ScaleX, 0, 0.2);
        _doc.AddEffectKeyframe(fx.Id, TransformProperty.ScaleX, 11, 1.6, KeyframeInterpolation.Linear);
        _doc.AddEffectKeyframe(fx.Id, TransformProperty.ScaleY, 0, 0.2);
        _doc.AddEffectKeyframe(fx.Id, TransformProperty.ScaleY, 11, 1.6, KeyframeInterpolation.Linear);
        _doc.AddEffectKeyframe(fx.Id, TransformProperty.Opacity, 0, 1.0);
        _doc.AddEffectKeyframe(fx.Id, TransformProperty.Opacity, 11, 0.0, KeyframeInterpolation.Linear);
        // Audio bed.
        _doc.AddAudio(wavPath, startSeconds: 0.5);

        // Missing-asset path must degrade, not crash.
        var bogus = _doc.AddEffect(Path.Combine(dir, "does-not-exist.png"), 0, 5);
        RebuildSequence();
        _doc.RemoveEffect(bogus.Id);

        // Undo/redo round trip on the audio add.
        if (!_doc.Undo())
            throw new InvalidOperationException("Undo failed in evidence run.");
        if (!_doc.Redo())
            throw new InvalidOperationException("Redo failed in evidence run.");

        SetSequenceMode(true);
        await Task.Delay(400);
        // Select the second clip to show clip selection + inspector state.
        var second = _seqResolved.Skip(1).FirstOrDefault();
        if (second is not null)
            OnSeqClipSelected(second.Clip.Id);

        string projectPath = Path.Combine(dir, "katana-combo.dcanim");
        ProjectSerializer.Save(_doc.Project, projectPath);
        _doc.MarkSaved(projectPath);
        UpdateTitle();

        // Round-trip: reopen the saved file through the real loader path.
        _doc.New();
        await OpenProjectFileAsync(projectPath);

        if (_seqRendered is null || _seqClip is null || _seqClip.FrameCount == 0)
            throw new InvalidOperationException("Sequence compose failed.");
        var rendered = new RenderedSequence(
            _seqRendered.Width, _seqRendered.Height,
            _seqClip.Frames.Select(f => f.Rgba).ToList());
        string pngDir = Path.Combine(dir, "png");
        PngSequenceExporter.ExportRendered(rendered, pngDir, overwrite: true);
        GifExporter.ExportRendered(rendered, _doc.Project.Fps, Path.Combine(dir, "combo.gif"), loop: true, overwrite: true);
        SpriteSheetExporter.Export(rendered, _seqResolved, _doc.Project.Fps,
            Path.Combine(dir, "sheet.png"), Path.Combine(dir, "sheet.json"),
            columns: 8, padding: 2, overwrite: true);
        await Task.CompletedTask;
    }

    /// <summary>Locally generated demo assets: shockwave ring + test tone.</summary>
    private static void GenerateEvidenceAssets(string dir, out string fxPath, out string wavPath)
    {
        Directory.CreateDirectory(dir);
        fxPath = Path.Combine(dir, "evidence-shockwave.png");
        using (var bmp = new SkiaSharp.SKBitmap(96, 96, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul))
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            using var paint = new SkiaSharp.SKPaint
            {
                Style = SkiaSharp.SKPaintStyle.Stroke,
                StrokeWidth = 6,
                Color = new SkiaSharp.SKColor(255, 220, 150, 255),
                IsAntialias = true,
            };
            canvas.DrawCircle(48, 48, 34, paint);
            using var soft = new SkiaSharp.SKPaint
            {
                Style = SkiaSharp.SKPaintStyle.Stroke,
                StrokeWidth = 14,
                Color = new SkiaSharp.SKColor(255, 200, 120, 70),
                IsAntialias = true,
            };
            canvas.DrawCircle(48, 48, 34, soft);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(fxPath, data.ToArray());
        }

        wavPath = Path.Combine(dir, "evidence-tone.wav");
        const int rate = 44100;
        const double seconds = 1.0;
        int samples = (int)(rate * seconds);
        using var stream = File.Create(wavPath);
        using var writer = new System.IO.BinaryWriter(stream);
        int dataBytes = samples * 2;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            double env = Math.Min(1.0, t * 20) * Math.Max(0.0, 1.0 - t / seconds);
            short s = (short)(Math.Sin(2 * Math.PI * 880 * t) * 9000 * env);
            writer.Write(s);
        }
    }

    private async Task MaybeRunEvidenceModeAsync()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--evidence");
        if (i < 0 || i + 1 >= args.Length)
            return;
        string dir = args[i + 1];
        try
        {
            Directory.CreateDirectory(dir);
            await Task.Delay(800);
            if (_loader is null)
                throw new InvalidOperationException("No animation source loaded; cannot capture evidence.");
            if (!_loader.Catalog.Contains("AtkKatanaB2"))
                throw new InvalidOperationException("AtkKatanaB2 not in catalog; cannot capture evidence.");
            Browser.Select("AtkKatanaB2"); // exercise the real selection path
            if (args.Contains("--grid"))
                Browser.SetViewMode(grid: true);
            int tl = Array.IndexOf(args, "--tlzoom");
            if (tl >= 0 && tl + 1 < args.Length && double.TryParse(args[tl + 1], out double tlz))
                Timeline.SetZoomFactor(tlz);
            await Task.Delay(3500); // let background thumbnail prefetch land
            if (args.Contains("--sequence"))
                await RunSequenceEvidenceAsync(dir);
            TogglePlay(); // capture mid-playback: Pause icon + advanced frame
            await Task.Delay(900);
            RefreshFrame();

            var pixelSize = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            using (var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96)))
            {
                rtb.Render(this);
                rtb.Save(Path.Combine(dir, "app-mainwindow.png"));
            }
            // Evidence capture is verified by the output files themselves.
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "EVIDENCE_ERROR.txt"), ex.ToString()); } catch { }
        }
        finally
        {
            await Task.Delay(300);
            Close();
        }
    }
}
