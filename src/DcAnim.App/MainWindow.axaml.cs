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
    private SequenceTiming.SpliceMap? _trimAnchorMap;
    private int _trimAnchorStart;
    private int _trimAnchorFirst;
    private int _trimAnchorLast;
    private bool _trimAnchorValid;

    // Weapon substitution editing state.
    private int _weaponSourceFrame = -1;
    private bool _weaponMove;
    private WeaponPose? _weaponDragBase;
    private int _weaponDragSrc = -1;
    private WriteableBitmap? _donorHoverBmp;
    private string? _donorHoverId;
    private readonly Stopwatch _donorHoverThrottle = new();

    // Frame-level editing state (global output frames, end-exclusive).
    private int _frameSelStart = -1;
    private int _frameSelEnd = -1;
    private bool HasFrameSelection => _seqMode && _frameSelStart >= 0 && _frameSelEnd > _frameSelStart;
    private List<SequenceDocument.SpliceSegment> _clipboard = new();

    private int SourceCount(string animationId) =>
        _loader!.Catalog.GetFrameRegions(animationId).Count;

    private Func<string, int> SourceCounter => SourceCount;

    private int EstimateLibraryOutputs(string animationId)
    {
        if (_loader is null)
            return 8;
        try
        {
            int sources = SourceCount(animationId);
            return SequenceTiming.ClipOutputFrames(Math.Max(1, sources), 1.0, _doc.Project.Fps, 0, 0);
        }
        catch
        {
            return 8;
        }
    }

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
        Timeline.SetLibraryEstimator(EstimateLibraryOutputs);
        Timeline.SeqClipSelected += OnSeqClipSelected;
        Timeline.SeqClipMoved += (id, i) => { _doc.MoveClip(id, i); AfterStructuralEdit(); };
        Timeline.SeqLibraryDrop += (animId, i) =>
        {
            if (_loader is null)
                return;
            int at = IndexStartFrame(i);
            StructuralEdit("Add clip", at, () =>
            {
                var c = _doc.AddClip(animId, 1.0, i);
                _seqSelectedClip = c.Id;
                SelectSeqTarget(SeqTargetKind.Clip, c.Id, seekToStart: false);
            });
        };
        Timeline.SeqRemoveRequested += id =>
        {
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == id);
            int at = r?.StartFrame ?? 0;
            StructuralEdit("Remove clip", at, () => _doc.RemoveClip(id));
        };
        Timeline.SeqDuplicateRequested += id =>
        {
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == id);
            int at = (r?.StartFrame ?? 0) + (r?.OutputFrames ?? 0);
            StructuralEdit("Duplicate clip", at, () =>
            {
                var c = _doc.DuplicateClip(id);
                _seqSelectedClip = c.Id;
            });
        };
        Timeline.SeqSpeedRequested += (id, s) =>
        {
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == id);
            int at = r?.StartFrame ?? 0;
            try
            {
                StructuralEdit($"Speed {s:0.##}×", at, () => _doc.SetClipSpeed(id, s));
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message);
            }
        };
        Timeline.SeqTrimDragStarted += (id, _) =>
        {
            _doc.BeginDrag("Trim clip");
            // Anchor the trim to the drag-start mapping. Recomputing from the
            // live resolved segment on every mouse move feeds back: shrinking
            // the clip changes OutputFrames/StartFrame, so the same cursor X
            // maps to a different source and collapses to one frame.
            _trimAnchorValid = false;
            var anchor = _seqResolved.FirstOrDefault(x => x.Clip.Id == id);
            if (anchor is not null && _loader is not null)
            {
                try
                {
                    int count = SourceCount(anchor.Clip.AnimationId);
                    _trimAnchorMap = anchor.Map;
                    _trimAnchorStart = anchor.StartFrame;
                    _trimAnchorFirst = anchor.SourceFirst;
                    _trimAnchorLast = anchor.SourceFirst + anchor.SourceFrames - 1;
                    _trimAnchorValid = count > 0 && anchor.SourceFrames > 0;
                }
                catch (KeyNotFoundException) { }
            }
        };
        Timeline.SeqTrimming += OnTrimming;
        Timeline.SeqTrimDragFinished += () => { _trimAnchorValid = false; _doc.EndDrag(); AfterStructuralEdit(); RefreshFrame(); };
        Timeline.SeqFrameRangeSelected += SetFrameSelection;
        Timeline.SeqSeekRequested += g =>
        {
            if (CanTransport)
            {
                ClearFrameSelection();
                _engine.SeekFrame(Math.Clamp(g, 0, Math.Max(0, ActiveFrameCount - 1)));
                RefreshFrame();
            }
        };
        Timeline.SeqSplitRequested += SplitAtPlayhead;
        Timeline.SeqReverseRequested += ReverseSelected;
        Timeline.SeqHoldRequested += h => SetHoldForSelection(h);
        Timeline.SeqFrameCopyRequested += CopySelection;
        Timeline.SeqFrameCutRequested += CutSelection;
        Timeline.SeqFramePasteRequested += PasteAtPlayhead;
        Timeline.SeqFrameDeleteRequested += DeleteSelection;
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
        Timeline.SeqDragCancelled += () => { _trimAnchorValid = false; _doc.AbortDrag(); RebuildSequence(); RefreshFrame(); };
        Viewport.Preview.MoveStarted += OnViewportDragStart;
        Viewport.Preview.MoveDelta += OnViewportMove;
        Viewport.Preview.MoveFinished += OnViewportDragEnd;
        Viewport.Preview.MoveCancelled += () => { _moveBase = null; _doc.AbortDrag(); RebuildSequence(); RefreshFrame(); };
        Viewport.Preview.DoubleTapped += (_, _) => FitPreview();
        SeqInspector.ClipSpeedChanged += s =>
        {
            if (_seqTarget != SeqTargetKind.Clip)
                return;
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == _seqTargetId);
            try
            {
                StructuralEdit($"Speed {s:0.##}×", r?.StartFrame ?? 0, () => _doc.SetClipSpeed(_seqTargetId, s));
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message);
            }
        };
        SeqInspector.ClipTrimInChanged += v => SetTrimForSelection(v, null);
        SeqInspector.ClipTrimOutChanged += v => SetTrimForSelection(null, v);
        SeqInspector.ClipReversedChanged += rev => SetReverseForSelection(rev);
        SeqInspector.ClipHoldChanged += h => SetHoldForSelection(h);
        SeqInspector.OnionEnabledChanged += on => SetOnionEnabled(on);
        SeqInspector.OnionCountChanged += n => SetOnionCount(n);
        SeqInspector.OnionOpacityChanged += o => SetOnionOpacity(o);
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
        SeqInspector.AudioVolumeChanged += v => { if (_seqTarget == SeqTargetKind.Audio) { _doc.SetAudioVolume(_seqTargetId, v); _audio.StopAll(); } };
        SeqInspector.AudioMutedChanged += m => { if (_seqTarget == SeqTargetKind.Audio) { _doc.SetAudioMuted(_seqTargetId, m); _audio.StopAll(); RebuildSequence(); } };
        SeqInspector.AudioRelinkRequested += async () => await RelinkAudioAsync();
        SeqInspector.AudioRemoveRequested += () => { if (_seqTarget == SeqTargetKind.Audio) { _doc.RemoveAudio(_seqTargetId); AfterStructuralEdit(); } };
        SeqInspector.WeaponEnabledChanged += enabled => SetWeaponEnabled(enabled);
        SeqInspector.WeaponPickDonorRequested += UseLibraryDonor;
        SeqInspector.WeaponAutoRequested += AutoDetectWeapon;
        SeqInspector.WeaponClearRequested += () => { if (_seqTarget == SeqTargetKind.Clip) { _doc.SetClipWeapon(_seqTargetId, null); _weaponSourceFrame = -1; RebuildSequence(); RefreshSequenceInspector(); } };
        SeqInspector.WeaponImportRequested += async () => await ImportWeaponPlanAsync();
        SeqInspector.WeaponExportRequested += async () => await ExportWeaponPlanAsync();
        SeqInspector.WeaponFrameStepRequested += StepWeaponFrame;
        SeqInspector.WeaponPoseApplied += pose => ApplyWeaponPose(pose);
        SeqInspector.WeaponFlipRequested += () => FlipWeapon(all: false);
        SeqInspector.WeaponFlipAllRequested += () => FlipWeapon(all: true);
        SeqInspector.WeaponMoveChanged += move => _weaponMove = move;
        SeqInspector.WeaponBehindChanged += behind => SetWeaponFlag(behind, null, null, null);
        SeqInspector.WeaponPreserveChanged += preserve => SetWeaponFlag(null, preserve, null, null);
        SeqInspector.WeaponNonBodyChanged += nonBody => SetWeaponFlag(null, null, nonBody, null);
        SeqInspector.WeaponBodyColorChanged += color => SetWeaponFlag(null, null, null, color);
        SeqInspector.WeaponDonorHovered += OnDonorHover;
        SeqInspector.WeaponDonorPicked += id => SeqInspector.SetDonor(id);
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
        OnionItem.Click += (_, _) => SetOnionEnabled(OnionItem.IsChecked == true);
        FitItem.Click += (_, _) => FitPreview();
        TimelineZoomInItem.Click += (_, _) => Timeline.ZoomIn();
        TimelineZoomOutItem.Click += (_, _) => Timeline.ZoomOut();
        ResetLayoutItem.Click += (_, _) => ResetLayout();
        SplitItem.Click += (_, _) => SplitAtPlayhead();
        CopyRangeItem.Click += (_, _) => CopySelection();
        CutRangeItem.Click += (_, _) => CutSelection();
        PasteRangeItem.Click += (_, _) => PasteAtPlayhead();
        DeleteRangeItem.Click += (_, _) => DeleteSelection();
        DuplicateRangeItem.Click += (_, _) => DuplicateSelection();
        ReverseItem.Click += (_, _) => ReverseSelected();
        RippleItem.Click += (_, _) => { _doc.SetRipple(RippleItem.IsChecked == true); UpdateRippleMenu(); };
        FormatNotesItem.Click += async (_, _) => await ShowFormatNotesAsync();
        ShortcutsItem.Click += async (_, _) => await ShowShortcutsAsync();
        AboutItem.Click += async (_, _) => await ShowAboutAsync();

        KeyDown += OnKeyDown;
        Closing += async (_, e) =>
        {
            if (IsEvidenceRun() || _closingConfirmed || _auditMode)
            {
                // Headless runs never persist workspace state.
                if (!IsEvidenceRun() && !_auditMode)
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
        Opened += (_, _) => _ = MaybeRunAuditAsync();
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
            // No game install: use the bundled default-outfit atlas shipped
            // with the app, so the editor works without Steam.
            string? bundled = GameSourceLocator.FindBundledAtlasDirectory();
            if (bundled is not null)
            {
                LoadFromDirectory(bundled);
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
        _nativeCache.Clear();
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
        RefreshDonorGallery();
        Transport.SetEnabled(false);
        Viewport.SetTitle("No animation", "Select an animation from the library");
        Viewport.SetFrame(null);
        Inspector.Clear();
        Timeline.Clear();
        SourceText.Text = loader.SourceDescription;
        Browser.Select(_preferredAnimation);
        // Resync sequence resolution with the new catalog. The overlay uses
        // _seqResolved source indexes with the current loader; without this,
        // a stale segment can request a frame index that is out of range for
        // the new atlas and crash preview on the UI thread.
        RebuildSequence();
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
            SeqInspector.ForEachDonorItem(item =>
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
        // In sequence mode the library is a drag source, not a viewer load.
        // Loading the viewer clip here would refit the shared timeline zoom
        // on every press-to-drag. Remember the choice and leave zoom alone.
        if (_seqMode)
        {
            _preferredAnimation = id;
            return;
        }
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
        if (clip.FrameCount == 0)
        {
            // Empty sequence: actively clear the viewport so no stale frame
            // lingers from the viewer or a previous composition.
            Viewport.SetFrame(null);
            Transport.SetPlaying(false);
            if (_seqMode)
            {
                UpdateSequenceSubtitle();
                UpdateViewportOverlay();
                SyncAudio();
            }
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
            UpdateOnionSkins();
            SyncAudio();
        }
        else
        {
            Timeline.SetPosition(_engine.CurrentFrameIndex, clip.FrameCount, _fps);
            Inspector.SetPosition(_engine.CurrentFrameIndex, clip.FrameCount, frame?.RegionName ?? "—");
        }
        Transport.SetPlaying(_engine.IsPlaying);
    }

    /// <summary>
    /// Onion-skin neighbors from the already-composed sequence bitmaps (no
    /// re-decode). Preview-only: exporters consume <see cref="_seqRendered"/>
    /// directly and never see these.
    /// </summary>
    private void UpdateOnionSkins()
    {
        if (!_settings.OnionEnabled || _seqClip is null || _seqClip.FrameCount == 0 ||
            _seqBitmaps.Count != _seqClip.FrameCount)
        {
            Viewport.Preview.OnionPrevious = null;
            Viewport.Preview.OnionNext = null;
            return;
        }
        var (prev, next) = TimelineMath.OnionFrames(
            _engine.CurrentFrameIndex, _seqClip.FrameCount, _settings.OnionCount);
        Viewport.Preview.OnionPrevious = prev.Select(i => (Bitmap)_seqBitmaps[i]).ToList();
        Viewport.Preview.OnionNext = next.Select(i => (Bitmap)_seqBitmaps[i]).ToList();
        Viewport.Preview.OnionOpacity = _settings.OnionOpacity;
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
            var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
            // The overlay describes the SELECTED clip, so it must use that
            // clip's own resolved segment. Using the playhead clip's segment
            // with the selected clip's animation asked for source indexes
            // valid for one animation but out of range for the other —
            // a per-frame crash during multi-clip playback.
            var r = clip is null
                ? null
                : _seqResolved.FirstOrDefault(r => r.Clip.Id == _seqTargetId);
            if (r is null || clip is null)
                return;
            // Only overlay while the playhead is inside the selected clip.
            if (g < r.StartFrame || g >= r.StartFrame + r.OutputFrames)
                return;
            if (_loader is null)
                return;
            int local = g - r.StartFrame;
            int src;
            try
            {
                src = r.Map.SourceAt(local);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }
            ClipFrame native;
            try
            {
                native = _nativeCache.Get(_loader, clip.AnimationId, src, _doc.Project.Fps);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException ||
                ex is KeyNotFoundException || ex is InvalidDataException)
            {
                return;
            }
            var t = clip.EvaluateAt(local);
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
        string splice = (r.Reversed ? " · REV" : "") +
            (r.TailHoldFrames > 0 ? $" · +{r.TailHoldFrames}H" : "") +
            (r.LeadHoldFrames > 0 ? $" · {r.LeadHoldFrames}H+" : "") +
            (HasFrameSelection ? $" · sel {_frameSelStart + 1}–{_frameSelEnd}" : "");
        Viewport.SetTitle(_doc.Project.Name,
            $"Clip {r.ClipIndex + 1}/{_seqResolved.Count} · {r.Clip.AnimationId} " +
            $"frame {local}/{r.OutputFrames}{splice} · {r.Clip.Speed:0.##}× · {_seqClip.Frames[0].Width}×{_seqClip.Frames[0].Height} px · {_doc.Project.Fps:0.#} FPS");
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
            ("Delete", "Delete selected frames, or selected clip / effect / audio"),
            ("Esc", "Clear frame selection"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo (sequence edits)"),
            ("Ctrl+K", "Split clip at playhead"),
            ("Ctrl+C / Ctrl+X / Ctrl+V", "Copy / cut / paste frames"),
            ("Ctrl+D", "Duplicate frames or clip"),
            ("R", "Reverse selected clip"),
            ("Shift+drag timeline", "Select a frame range"),
            ("Ruler click", "Seek to exact frame"),
            ("Drag clip edges", "Trim source In/Out"),
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
                case Key.K:
                    SplitAtPlayhead();
                    e.Handled = true;
                    break;
                case Key.C:
                    CopySelection();
                    e.Handled = true;
                    break;
                case Key.X:
                    CutSelection();
                    e.Handled = true;
                    break;
                case Key.V:
                    PasteAtPlayhead();
                    e.Handled = true;
                    break;
                case Key.D:
                    DuplicateSelection();
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
            case Key.Escape when _frameSelStart >= 0:
                ClearFrameSelection();
                e.Handled = true;
                break;
            case Key.R when _seqMode:
                ReverseSelected();
                e.Handled = true;
                break;
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
                    if (HasFrameSelection)
                    {
                        DeleteSelection();
                        e.Handled = true;
                    }
                    else if (_seqTarget == SeqTargetKind.Clip && _seqTargetId is { } dc2)
                    {
                        var rr = _seqResolved.FirstOrDefault(x => x.Clip.Id == dc2);
                        StructuralEdit("Remove clip", rr?.StartFrame ?? 0, () => _doc.RemoveClip(dc2));
                        e.Handled = true;
                    }
                    else if (_seqTarget == SeqTargetKind.Effect && _seqTargetId is { } de)
                    {
                        _doc.RemoveEffect(de);
                        AfterStructuralEdit();
                        e.Handled = true;
                    }
                    else if (_seqTarget == SeqTargetKind.Audio && _seqTargetId is { } da)
                    {
                        _doc.RemoveAudio(da);
                        AfterStructuralEdit();
                        e.Handled = true;
                    }
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
        OnionItem.IsChecked = _settings.OnionEnabled;
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
            if (_clip.Id != _preferredAnimation && _loader is not null)
                SelectAnimation(_preferredAnimation);
            else
            {
                _engine.Load(_clip);
                _engine.Pause();
            }
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
    /// Composition failures (oversized canvas, corrupt assets) leave the
    /// previous valid state in place and surface a toast — never a crash,
    /// never a half-built timeline.
    /// </summary>
    private void RebuildSequence(bool selectLastAdded = false)
    {
        if (_loader is null)
            return;
        WarnMissingAssets();

        int keepGlobal = _engine.CurrentFrameIndex;
        IReadOnlyList<ResolvedClip> resolved;
        RenderedSequence rendered;
        var bitmaps = new List<WriteableBitmap>();
        try
        {
            resolved = SequenceComposer.Resolve(_doc.Project, id => _loader.Catalog.GetFrameRegions(id).Count);
            if (resolved.Count == 0 || resolved.Sum(r => r.OutputFrames) == 0)
            {
                // Empty sequence: nothing to render. Keep a 0-frame clip so the
                // engine, timeline and inspector all see a valid empty state.
                rendered = new RenderedSequence(0, 0, Array.Empty<byte[]>());
                resolved = Array.Empty<ResolvedClip>();
            }
            else
            {
                rendered = SequenceCompositor.Render(_doc.Project, _loader, _nativeCache, _fxCache, out var r2);
                resolved = r2;
            }

            var frames = new List<ClipFrame>(rendered.Frames.Count);
            for (int i = 0; i < rendered.Frames.Count; i++)
            {
                frames.Add(new ClipFrame
                {
                    Index = i,
                    RegionName = $"seq_{i:0000}",
                    Width = rendered.Width,
                    Height = rendered.Height,
                    Rgba = rendered.Frames[i],
                    Duration = TimeSpan.FromSeconds(1.0 / _doc.Project.Fps),
                });
                bitmaps.Add(FrameBitmap.FromRgba(rendered.Frames[i], rendered.Width, rendered.Height));
            }
            var clip = new AnimationClip
            {
                Id = _doc.Project.Name,
                Frames = frames,
                SourceDescription = $"Sequence '{_doc.Project.Name}' ({resolved.Count} clips, {frames.Count} frames @ {_doc.Project.Fps:0.#} FPS)",
                DefaultFps = _doc.Project.Fps,
            };

            // Commit: swap in the fully built state only.
            ClearSeqCache();
            foreach (var bmp in bitmaps)
                _seqBitmaps.Add(bmp);
            bitmaps.Clear();
            _seqRendered = rendered;
            _seqResolved = resolved;
            _seqClip = clip;
        }
        catch (Exception ex)
        {
            foreach (var bmp in bitmaps)
                bmp.Dispose();
            ShowToast($"Could not compose sequence: {ex.Message}");
            return;
        }

        if (_seqSelectedClip is null || _seqResolved.All(r => r.Clip.Id != _seqSelectedClip))
            _seqSelectedClip = _seqResolved.Count > 0 ? _seqResolved[^1].Clip.Id : null;
        if (selectLastAdded && _seqResolved.Count > 0)
            _seqSelectedClip = _seqResolved[^1].Clip.Id;
        if (_seqTarget != SeqTargetKind.None && !SeqTargetExists())
            ClearSeqTarget();
        ValidateKeySelection();

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
            int total = 0;
            try { total = _loader is null ? 0 : SourceCount(r.Clip.AnimationId); } catch { }
            blocks.Add(new SequenceTrackControl.ClipBlock(
                r.Clip.Id, r.Clip.AnimationId, r.StartFrame, r.OutputFrames, thumbs,
                r.SourceFirst, r.SourceFrames, total, r.Reversed,
                r.LeadHoldFrames + r.TailHoldFrames));
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
        // Unknown ids are ignored: selection must always reference something real.
        bool known = kind switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.Any(c => c.Id == id),
            SeqTargetKind.Effect => _doc.Project.Effects.Any(c => c.Id == id),
            SeqTargetKind.Audio => _doc.Project.Audio.Any(c => c.Id == id),
            _ => false,
        };
        if (!known)
            return;
        _seqTarget = kind;
        _seqTargetId = id;
        _seqKeyProp = null;
        if (kind == SeqTargetKind.Clip)
            _seqSelectedClip = id;
        if (seekToStart && _seqClip is { FrameCount: > 0 })
        {
            int start = kind switch
            {
                SeqTargetKind.Clip => _seqResolved.FirstOrDefault(r => r.Clip.Id == id)?.StartFrame ?? 0,
                SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == id)?.StartFrame ?? 0,
                SeqTargetKind.Audio => (int)Math.Round(
                    (_doc.Project.Audio.FirstOrDefault(a => a.Id == id)?.StartSeconds ?? 0) * _doc.Project.Fps),
                _ => _engine.CurrentFrameIndex,
            };
            _engine.SeekFrame(Math.Clamp(start, 0, _seqClip.FrameCount - 1));
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
        ValidateFrameSelection();
        var blocks = BuildBlocks();
        var fxBlocks = BuildFxBlocks();
        var auBlocks = BuildAudioBlocks();
        Timeline.SetSequenceData(blocks, fxBlocks, auBlocks,
            _seqTarget == SeqTargetKind.Clip ? _seqTargetId : _seqSelectedClip,
            _seqTarget == SeqTargetKind.Effect ? _seqTargetId : null,
            _seqTarget == SeqTargetKind.Audio ? _seqTargetId : null,
            BuildKeyMarkers(), BuildSelectedKey(),
            showEffectKeys: _seqTarget != SeqTargetKind.Clip,
            currentFrame, _doc.Project.Fps,
            HasFrameSelection ? _frameSelStart : -1,
            HasFrameSelection ? _frameSelEnd : -1);
    }

    private void AfterStructuralEdit()
    {
        if (_seqTarget != SeqTargetKind.None && !SeqTargetExists())
            ClearSeqTarget();
        RebuildSequence();
    }

    private int TotalSeqFrames() => _seqResolved.Sum(r => r.OutputFrames);

    private int IndexStartFrame(int clipIndex)
    {
        if (clipIndex >= _seqResolved.Count)
            return TotalSeqFrames();
        if (clipIndex <= 0)
            return 0;
        return _seqResolved[clipIndex].StartFrame;
    }

    /// <summary>
    /// Run a timeline-mutating edit plus ripple as one undo step, then
    /// refresh every surface. No-op with a toast when no game source is
    /// loaded; failures abort the group and toast instead of crashing.
    /// </summary>
    private void StructuralEdit(string label, int ripplePoint, Action edit)
    {
        if (_loader is null)
        {
            ShowToast("Open a game source first.");
            return;
        }
        int before;
        try
        {
            before = SequenceComposer.TotalFrames(_doc.Project, SourceCounter);
        }
        catch (Exception ex)
        {
            ShowToast($"Cannot resolve sequence: {ex.Message}");
            return;
        }
        _doc.BeginGroup(label);
        try
        {
            edit();
            int after = SequenceComposer.TotalFrames(_doc.Project, SourceCounter);
            if (_doc.Project.RippleFxAudio && after != before)
                _doc.ShiftFxAudio(ripplePoint, after - before);
            _doc.EndGroupIfChanged(); // no-op edits leave no undo step
        }
        catch (Exception ex)
        {
            _doc.AbortGroup();
            ShowToast(ex.Message);
        }
        AfterStructuralEdit();
        RefreshFrame();
    }

    // ---------- frame selection ----------

    private void SetFrameSelection(int start, int end)
    {
        int total = TotalSeqFrames();
        if (total <= 0)
        {
            ClearFrameSelection();
            return;
        }
        _frameSelStart = Math.Clamp(start, 0, total - 1);
        _frameSelEnd = Math.Clamp(end, _frameSelStart + 1, total);
        RefreshSequenceTimeline(_engine.CurrentFrameIndex);
    }

    private void ClearFrameSelection()
    {
        if (_frameSelStart < 0)
            return;
        _frameSelStart = _frameSelEnd = -1;
        RefreshSequenceTimeline(_engine.CurrentFrameIndex);
    }

    private void ValidateFrameSelection()
    {
        int total = TotalSeqFrames();
        if (!HasFrameSelection || total <= 0)
        {
            _frameSelStart = _frameSelEnd = -1;
            return;
        }
        _frameSelStart = Math.Clamp(_frameSelStart, 0, total - 1);
        _frameSelEnd = Math.Clamp(_frameSelEnd, _frameSelStart + 1, total);
    }

    // ---------- splicing operations ----------

    private void OnTrimming(string clipId, bool isLeft, int globalFrame)
    {
        try
        {
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == clipId);
            if (r is null || _loader is null)
                return;
            int count = SourceCount(r.Clip.AnimationId);
            var clip = r.Clip;
            double speed = clip.Speed;
            double fps = _doc.Project.Fps;
            int lead = Math.Max(0, clip.LeadHoldFrames);
            int tail = Math.Max(0, clip.TailHoldFrames);
            if (_trimAnchorValid && _trimAnchorMap is { } anchorMap)
            {
                int anchorEnd = _trimAnchorStart + anchorMap.TotalOutputs - 1;
                if (isLeft)
                {
                    if (globalFrame >= _trimAnchorStart)
                    {
                        int local = Math.Clamp(globalFrame - _trimAnchorStart, 0, Math.Max(0, anchorMap.TotalOutputs - 1));
                        int src = anchorMap.SourceAt(local);
                        _doc.SetTrim(clipId, Math.Min(src, _trimAnchorLast), clip.SourceLast, count);
                    }
                    else
                    {
                        // Extend left: grow the visible range to reach the cursor.
                        int desiredOutputs = anchorEnd - globalFrame + 1;
                        int maxVisible = _trimAnchorLast + 1;
                        int maxOutputs = SequenceTiming.ClipOutputFrames(Math.Max(1, maxVisible), speed, fps, lead, tail);
                        desiredOutputs = Math.Clamp(desiredOutputs, 1, Math.Max(1, maxOutputs));
                        int baseOutputs = Math.Max(1, desiredOutputs - lead - tail);
                        int desiredVisible = Math.Clamp((int)Math.Round(baseOutputs * SequenceTiming.NativeFps * speed / fps, MidpointRounding.AwayFromZero), 1, Math.Max(1, maxVisible));
                        int newFirst = Math.Clamp(_trimAnchorLast - desiredVisible + 1, 0, _trimAnchorLast);
                        _doc.SetTrim(clipId, newFirst, clip.SourceLast, count);
                    }
                }
                else
                {
                    if (globalFrame <= anchorEnd)
                    {
                        int local = Math.Clamp(globalFrame - _trimAnchorStart, 0, Math.Max(0, anchorMap.TotalOutputs - 1));
                        int src = anchorMap.SourceAt(local);
                        _doc.SetTrim(clipId, _trimAnchorFirst, Math.Max(src, _trimAnchorFirst), count);
                    }
                    else
                    {
                        // Extend right: grow the visible range to reach the cursor.
                        int desiredOutputs = globalFrame - _trimAnchorStart + 1;
                        int maxVisible = Math.Max(1, count - _trimAnchorFirst);
                        int maxOutputs = SequenceTiming.ClipOutputFrames(maxVisible, speed, fps, lead, tail);
                        desiredOutputs = Math.Clamp(desiredOutputs, 1, Math.Max(1, maxOutputs));
                        int baseOutputs = Math.Max(1, desiredOutputs - lead - tail);
                        int desiredVisible = Math.Clamp((int)Math.Round(baseOutputs * SequenceTiming.NativeFps * speed / fps, MidpointRounding.AwayFromZero), 1, maxVisible);
                        int newLast = Math.Clamp(_trimAnchorFirst + desiredVisible - 1, _trimAnchorFirst, count - 1);
                        _doc.SetTrim(clipId, _trimAnchorFirst, newLast, count);
                    }
                }
            }
            else
            {
                int local = Math.Clamp(globalFrame - r.StartFrame, 0, Math.Max(0, r.OutputFrames - 1));
                int src = r.Map.SourceAt(local);
                int resolvedLast = r.SourceFirst + r.SourceFrames - 1;
                if (isLeft)
                    _doc.SetTrim(clipId, Math.Min(src, resolvedLast), clip.SourceLast, count);
                else
                    _doc.SetTrim(clipId, clip.SourceFirst, Math.Max(src, clip.SourceFirst), count);
            }
            RebuildSequence();
            RefreshFrame();
        }
        catch (Exception ex)
        {
            _doc.AbortDrag();
            ShowToast(ex.Message);
            RebuildSequence();
        }
    }

    private void SplitAtPlayhead()
    {
        if (!_seqMode || _loader is null)
            return;
        int g = _engine.CurrentFrameIndex;
        if (g < 0)
        {
            ShowToast("Nothing to split: the sequence is empty.");
            return;
        }
        SequenceDocument.SplitResult? result = null;
        StructuralEdit("Split clip", g, () =>
        {
            result = _doc.SplitAt(g, SourceCounter);
        });
        if (result is null)
        {
            ShowToast("Playhead is at a clip edge — nothing to split.");
            return;
        }
        var right = _seqResolved.FirstOrDefault(r => r.Clip.Id == result.RightId);
        if (right is not null)
        {
            _engine.SeekFrame(Math.Min(right.StartFrame, Math.Max(0, TotalSeqFrames() - 1)));
            SelectSeqTarget(SeqTargetKind.Clip, result.RightId, seekToStart: false);
            RefreshFrame();
        }
        if (result.DroppedKeys > 0)
            ShowToast($"Split dropped {result.DroppedKeys} out-of-range keyframe(s); undo restores them.");
    }

    private bool TryFrameRange(out int start, out int end)
    {
        start = _frameSelStart;
        end = _frameSelEnd;
        return HasFrameSelection && end <= TotalSeqFrames();
    }

    private void CopySelection()
    {
        if (!_seqMode || _loader is null)
            return;
        try
        {
            List<SequenceDocument.SpliceSegment> segments;
            if (TryFrameRange(out int s, out int e))
            {
                segments = _doc.CopyRange(s, e, SourceCounter).ToList();
            }
            else
            {
                var r = _seqTarget == SeqTargetKind.Clip
                    ? _seqResolved.FirstOrDefault(x => x.Clip.Id == _seqTargetId)
                    : _seqResolved.FirstOrDefault(x => x.Clip.Id == _seqSelectedClip);
                if (r is null)
                {
                    ShowToast("Select frames or a clip first.");
                    return;
                }
                segments = _doc.CopyRange(r.StartFrame, r.StartFrame + r.OutputFrames, SourceCounter).ToList();
            }
            if (segments.Count == 0)
            {
                ShowToast("Nothing to copy: the range is empty.");
                return;
            }
            _clipboard = segments;
            ShowToast($"Copied {segments.Count} segment(s), {segments.Sum(x => x.SourceLast - x.SourceFirst + 1)} source frames.");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
    }

    private void CutSelection()
    {
        if (!_seqMode || _loader is null)
            return;
        CopySelection();
        if (_clipboard.Count == 0)
            return;
        DeleteSelection();
    }

    private void PasteAtPlayhead()
    {
        if (!_seqMode || _loader is null)
            return;
        if (_clipboard.Count == 0)
        {
            ShowToast("Clipboard is empty: copy frames first.");
            return;
        }
        int at = Math.Max(0, _engine.CurrentFrameIndex);
        int pastedTotal = 0;
        foreach (var s in _clipboard)
        {
            pastedTotal += SequenceTiming.ClipOutputFrames(
                s.SourceLast - s.SourceFirst + 1, s.Speed, _doc.Project.Fps,
                s.LeadHoldFrames, s.TailHoldFrames);
        }
        List<SequenceClip>? created = null;
        StructuralEdit($"Paste {pastedTotal} frames", at, () =>
        {
            created = _doc.PasteSegments(at, _clipboard, SourceCounter).ToList();
        });
        if (created is { Count: > 0 })
        {
            var last = created[^1];
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == last.Id);
            _seqSelectedClip = last.Id;
            SelectSeqTarget(SeqTargetKind.Clip, last.Id, seekToStart: false);
            if (r is not null)
                _engine.SeekFrame(Math.Min(r.StartFrame + r.OutputFrames, Math.Max(0, TotalSeqFrames() - 1)));
            ClearFrameSelection();
            RefreshFrame();
        }
    }

    private void DeleteSelection()
    {
        if (!_seqMode || _loader is null)
            return;
        if (TryFrameRange(out int s, out int e))
        {
            int dropped = 0;
            StructuralEdit("Delete frames", s, () =>
            {
                dropped = _doc.DeleteRange(s, e, SourceCounter);
            });
            if (dropped > 0)
                ShowToast($"Delete dropped {dropped} out-of-range keyframe(s); undo restores them.");
            ClearFrameSelection();
            _engine.SeekFrame(Math.Min(Math.Max(0, s), Math.Max(0, TotalSeqFrames() - 1)));
            RefreshFrame();
            return;
        }
        // Fall back to the classic target removal.
        if (_seqTarget == SeqTargetKind.Clip && _seqTargetId is { } dc)
        {
            var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == dc);
            StructuralEdit("Remove clip", r?.StartFrame ?? 0, () => _doc.RemoveClip(dc));
        }
        else if (_seqTarget == SeqTargetKind.Effect && _seqTargetId is { } de)
        {
            _doc.RemoveEffect(de);
            AfterStructuralEdit();
        }
        else if (_seqTarget == SeqTargetKind.Audio && _seqTargetId is { } da)
        {
            _doc.RemoveAudio(da);
            AfterStructuralEdit();
        }
    }

    private void DuplicateSelection()
    {
        if (!_seqMode || _loader is null)
            return;
        if (TryFrameRange(out int s, out int e))
        {
            var segments = _doc.CopyRange(s, e, SourceCounter).ToList();
            if (segments.Count == 0)
            {
                ShowToast("Nothing to duplicate.");
                return;
            }
            StructuralEdit("Duplicate frames", e, () =>
            {
                _doc.PasteSegments(e, segments, SourceCounter);
            });
            ClearFrameSelection();
            return;
        }
        var r = _seqTarget == SeqTargetKind.Clip
            ? _seqResolved.FirstOrDefault(x => x.Clip.Id == _seqTargetId)
            : null;
        if (r is null)
        {
            ShowToast("Select frames or a clip first.");
            return;
        }
        int at = r.StartFrame + r.OutputFrames;
        StructuralEdit("Duplicate clip", at, () =>
        {
            var c = _doc.DuplicateClip(r.Clip.Id);
            _seqSelectedClip = c.Id;
        });
    }

    private void ReverseSelected()
    {
        if (!_seqMode || _loader is null)
            return;
        // A frame range reverses every clip it touches; otherwise the
        // selected clip reverses.
        List<(string Id, int Start)> targets = new();
        if (TryFrameRange(out int s, out int e))
        {
            foreach (var r in _seqResolved)
            {
                if (e > r.StartFrame && s < r.StartFrame + r.OutputFrames)
                    targets.Add((r.Clip.Id, r.StartFrame));
            }
            if (targets.Count == 0)
            {
                ShowToast("Nothing to reverse in the selected range.");
                return;
            }
        }
        else if (_seqTarget == SeqTargetKind.Clip && _seqResolved.Any(x => x.Clip.Id == _seqTargetId))
        {
            var r = _seqResolved.First(x => x.Clip.Id == _seqTargetId);
            targets.Add((r.Clip.Id, r.StartFrame));
        }
        else
        {
            ShowToast("Select a clip or a frame range first.");
            return;
        }
        int dropped = 0;
        StructuralEdit(targets.Count > 1 ? $"Reverse {targets.Count} clips" : "Reverse clip",
            targets.Min(t => t.Start), () =>
            {
                foreach (var (id, _) in targets)
                {
                    var clip = _doc.Project.Clips.First(x => x.Id == id);
                    int count = SourceCount(clip.AnimationId);
                    dropped += _doc.SetReversed(id, !clip.Reversed, count);
                }
            });
        if (dropped > 0)
            ShowToast($"Reverse dropped {dropped} keyframe(s); undo restores them.");
    }

    private void SetTrimForSelection(int? oneBasedFirst, int? oneBasedLast)
    {
        if (!_seqMode || _loader is null || _seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip is null)
            return;
        int count;
        try { count = SourceCount(clip.AnimationId); }
        catch (Exception ex) { ShowToast(ex.Message); return; }
        int first = oneBasedFirst.HasValue ? oneBasedFirst.Value - 1 : clip.SourceFirst;
        int? last = oneBasedLast.HasValue ? oneBasedLast.Value - 1 : clip.SourceLast;
        var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == clip.Id);
        int at = r?.StartFrame ?? 0;
        int dropped = 0;
        try
        {
            StructuralEdit("Trim clip", at, () =>
            {
                dropped = _doc.SetTrim(clip.Id, first, last, count);
            });
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return;
        }
        if (dropped > 0)
            ShowToast($"Trim dropped {dropped} out-of-range keyframe(s); undo restores them.");
    }

    private void SetReverseForSelection(bool reversed)
    {
        if (!_seqMode || _loader is null || _seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip is null)
            return;
        int count;
        try { count = SourceCount(clip.AnimationId); }
        catch (Exception ex) { ShowToast(ex.Message); return; }
        var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == clip.Id);
        int dropped = 0;
        try
        {
            StructuralEdit(reversed ? "Reverse clip" : "Unreverse clip", r?.StartFrame ?? 0, () =>
            {
                dropped = _doc.SetReversed(clip.Id, reversed, count);
            });
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return;
        }
        if (dropped > 0)
            ShowToast($"Reverse dropped {dropped} keyframe(s); undo restores them.");
        RefreshSequenceInspector();
    }

    private void SetHoldForSelection(int holdFrames)
    {
        if (!_seqMode || _loader is null)
            return;
        var r = _seqTarget == SeqTargetKind.Clip
            ? _seqResolved.FirstOrDefault(x => x.Clip.Id == _seqTargetId)
            : null;
        if (r is null)
        {
            ShowToast("Select a clip first.");
            return;
        }
        int count;
        try { count = SourceCount(r.Clip.AnimationId); }
        catch (Exception ex) { ShowToast(ex.Message); return; }
        int validated = Math.Clamp(holdFrames, 0, SequenceClip.MaxHoldFrames);
        int dropped = 0;
        try
        {
            StructuralEdit(validated == 0 ? "Clear hold" : $"Hold {validated}f", r.StartFrame, () =>
            {
                dropped = _doc.SetTailHold(r.Clip.Id, validated, count);
            });
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return;
        }
        if (dropped > 0)
            ShowToast($"Hold change dropped {dropped} keyframe(s); undo restores them.");
    }

    private void SetOnionEnabled(bool enabled)
    {
        _settings.OnionEnabled = enabled;
        _settings.Save();
        OnionItem.IsChecked = enabled;
        RefreshSequenceInspector();
        RefreshFrame();
    }

    private void SetOnionCount(int count)
    {
        _settings.OnionCount = Math.Clamp(count, 1, 3);
        _settings.Save();
        RefreshSequenceInspector();
        RefreshFrame();
    }

    private void SetOnionOpacity(double opacity)
    {
        _settings.OnionOpacity = Math.Clamp(opacity, 0.15, 0.6);
        _settings.Save();
        RefreshFrame();
    }

    private void UpdateRippleMenu() => RippleItem.IsChecked = _doc.Project.RippleFxAudio;

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
        // The target may have been deleted mid-gesture; abort cleanly.
        bool alive = _seqTarget switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.Any(c => c.Id == _seqTargetId),
            SeqTargetKind.Effect => _doc.Project.Effects.Any(c => c.Id == _seqTargetId),
            _ => false,
        };
        if (!alive)
        {
            ClearSeqTarget();
            RebuildSequence();
            return;
        }
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

    private void ValidateKeySelection()
    {
        if (_seqKeyProp is null)
            return;
        IReadOnlyList<TransformKeyframe>? keys = _seqTarget switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId)?.Keyframes,
            SeqTargetKind.Effect => _doc.Project.Effects.FirstOrDefault(e => e.Id == _seqTargetId)?.Keyframes,
            _ => null,
        };
        if (keys is null || !keys.Any(k => k.Property == _seqKeyProp && k.Frame == _seqKeyFrame))
            _seqKeyProp = null;
    }

    private void OnViewportDragStart()
    {
        if (!_seqMode)
            return;
        // Weapon drag takes precedence when armed: move the donor pose, not the clip.
        if (_weaponMove && _seqTarget == SeqTargetKind.Clip)
        {
            var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
            if (clip?.Weapon is { Enabled: true })
            {
                var resolved = _seqResolved.FirstOrDefault(r => r.Clip.Id == clip.Id);
                int src = CurrentWeaponSourceFrame(clip, resolved);
                var frame = clip.Weapon.ForSource(src);
                if (frame is not null)
                {
                    _weaponDragBase = frame.Pose;
                    _weaponDragSrc = src;
                    _moveThrottle.Restart();
                    _doc.BeginDrag("Move weapon");
                    return;
                }
            }
        }
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
        if (!_seqMode)
            return;
        if (_weaponDragBase.HasValue)
        {
            var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
            if (clip is null || clip.Weapon is null)
            {
                _weaponDragBase = null;
                _doc.AbortDrag();
                RebuildSequence();
                return;
            }
            if (_moveThrottle.IsRunning && _moveThrottle.ElapsedMilliseconds < 40)
                return;
            _moveThrottle.Restart();
            double wzoom = Math.Max(1.0, Viewport.CurrentZoom);
            var b = _weaponDragBase.Value;
            try
            {
                _doc.SetWeaponPoseDirect(_seqTargetId, _weaponDragSrc,
                    b with { X = b.X + total.X / wzoom, Y = b.Y + total.Y / wzoom });
            }
            catch { return; }
            RebuildSequence();
            RefreshFrame();
            return;
        }
        if (_moveBase is null)
            return;
        // The target may have been deleted mid-gesture (e.g. undo); abort cleanly.
        bool alive = _seqTarget switch
        {
            SeqTargetKind.Clip => _doc.Project.Clips.Any(c => c.Id == _seqTargetId),
            SeqTargetKind.Effect => _doc.Project.Effects.Any(c => c.Id == _seqTargetId),
            _ => false,
        };
        if (!alive)
        {
            _moveBase = null;
            _doc.AbortDrag();
            RebuildSequence();
            return;
        }
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
        if (_weaponDragBase.HasValue)
        {
            _weaponDragBase = null;
            _doc.EndDrag();
            RebuildSequence();
            RefreshFrame();
            RefreshSequenceInspector();
            return;
        }
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
                int clipSources = SourceCountOrMinusOne(clip.AnimationId);
                SeqInspector.ShowClip(clip.AnimationId, clip.Speed, r.OutputFrames, _doc.Project.Fps,
                    clip.BaseTransform, clip.Keyframes,
                    r.SourceFirst + 1, r.SourceFirst + r.SourceFrames, clipSources,
                    r.Reversed, r.TailHoldFrames);
                SeqInspector.ShowWeapon(clip.Weapon, CurrentWeaponSourceFrame(clip, r), clip.Weapon?.Frames.Count ?? 0);
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
                        clip.BaseTransform, clip.Keyframes,
                        r.SourceFirst + 1, r.SourceFirst + r.SourceFrames, SourceCountOrMinusOne(clip.AnimationId),
                        r.Reversed, r.TailHoldFrames);
                    SeqInspector.ShowWeapon(clip.Weapon, CurrentWeaponSourceFrame(clip, r), clip.Weapon?.Frames.Count ?? 0);
                }
                else
                {
                    SeqInspector.ShowNothing();
                }
                break;
            }
        }
        SeqInspector.ShowProject(_doc.Project.Name, _doc.Project.Fps,
            _seqResolved.Count, _seqClip?.FrameCount ?? 0,
            _settings.OnionEnabled, _settings.OnionCount, _settings.OnionOpacity);
        UpdateRippleMenu();
        OnionItem.IsChecked = _settings.OnionEnabled;
    }

    private int SourceCountOrMinusOne(string animationId)
    {
        if (_loader is null)
            return -1;
        try { return SourceCount(animationId); }
        catch { return -1; }
    }

    // ---------- weapon substitution editing ----------

    private int CurrentWeaponSourceFrame(SequenceClip clip, ResolvedClip? resolved = null)
    {
        // Prefer what you're actually looking at: when the playhead is inside
        // the selected clip, that source frame wins over any cached stepping
        // position. Otherwise fall back to the stepped frame, then the first pose.
        if (resolved is not null && _seqMode && _seqClip is { FrameCount: > 0 })
        {
            int g = _engine.CurrentFrameIndex;
            if (g >= resolved.StartFrame && g < resolved.StartFrame + resolved.OutputFrames)
            {
                try
                {
                    int src = resolved.Map.SourceAt(g - resolved.StartFrame);
                    _weaponSourceFrame = src;
                    return src;
                }
                catch { }
            }
        }
        if (_weaponSourceFrame >= 0 && clip.Weapon?.ForSource(_weaponSourceFrame) is not null)
            return _weaponSourceFrame;
        return clip.Weapon?.Frames.FirstOrDefault()?.SourceFrame ?? clip.SourceFirst;
    }

    private void SetWeaponEnabled(bool enabled)
    {
        if (_seqTarget != SeqTargetKind.Clip || _loader is null)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip is null)
            return;
        if (!enabled)
        {
            if (clip.Weapon is not null)
            {
                var off = SequenceDocument.CloneWeapon(clip.Weapon)!;
                off.Enabled = false;
                _doc.SetClipWeapon(clip.Id, off);
                RebuildSequence();
                RefreshSequenceInspector();
            }
            return;
        }
        if (clip.Weapon is { Enabled: true })
            return;
        AutoDetectWeapon();
    }

    private void UseLibraryDonor()
    {
        string id = Browser.SelectedId ?? _preferredAnimation;
        if (string.IsNullOrWhiteSpace(id))
            return;
        SeqInspector.SetDonor(id);
        ShowToast($"Donor set to '{id}' — click Auto-detect to build poses.");
    }

    private void AutoDetectWeapon()
    {
        if (_seqTarget != SeqTargetKind.Clip || _loader is null)
        {
            ShowToast("Select a clip on the timeline first.");
            return;
        }
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip is null)
            return;
        string donorId = SeqInspector.WeaponDonorId;
        if (string.IsNullOrWhiteSpace(donorId))
            donorId = Browser.SelectedId ?? _preferredAnimation;
        if (string.IsNullOrWhiteSpace(donorId))
        {
            ShowToast("Enter a donor animation ID first.");
            return;
        }
        int donorFrame = Math.Max(0, SeqInspector.WeaponDonorFrameValue);
        int bodyGreen = SeqInspector.WeaponBodyGreenValue;
        int donorGreen = SeqInspector.WeaponDonorGreenValue;
        bool nonBody = SeqInspector.WeaponNonBodyValue;
        int bodyColor = SeqInspector.WeaponBodyColorValue;
        try
        {
            var resolved = _seqResolved.FirstOrDefault(r => r.Clip.Id == clip.Id);
            int first = resolved?.SourceFirst ?? clip.SourceFirst;
            int count = resolved?.SourceFrames ?? SourceCountOrMinusOne(clip.AnimationId);
            if (count <= 0)
            {
                int total = SourceCountOrMinusOne(clip.AnimationId);
                if (total <= 0) { ShowToast($"Cannot resolve '{clip.AnimationId}'."); return; }
                count = Math.Max(1, Math.Min(total - first, total));
            }
            int last = first + count - 1;
            var bodies = new List<(int, RgbaImage)>();
            for (int s = first; s <= last; s++)
            {
                try
                {
                    var f = _nativeCache.Get(_loader, clip.AnimationId, s, _doc.Project.Fps);
                    bodies.Add((s, new RgbaImage(f.Width, f.Height, (byte[])f.Rgba.Clone())));
                }
                catch { }
            }
            if (bodies.Count == 0) { ShowToast("No decodable body frames."); return; }
            int donorCount = SourceCountOrMinusOne(donorId);
            if (donorCount <= 0) { ShowToast($"Donor '{donorId}' not found."); return; }
            var df = _nativeCache.Get(_loader, donorId, Math.Clamp(donorFrame, 0, donorCount - 1), _doc.Project.Fps);
            var donorImg = new RgbaImage(df.Width, df.Height, (byte[])df.Rgba.Clone());
            var weapon = WeaponAutoDetect.Build(donorId, Math.Clamp(donorFrame, 0, donorCount - 1),
                bodyGreen, donorGreen, bodies, donorImg,
                preserveBody: true, behindBody: false, nonBodyMode: nonBody, bodyColorGreen: bodyColor);
            _doc.SetClipWeapon(clip.Id, weapon);
            _weaponSourceFrame = weapon.Frames[0].SourceFrame;
            RebuildSequence();
            RefreshSequenceInspector();
            RefreshFrame();
        }
        catch (Exception ex)
        {
            ShowToast($"Weapon auto-detect failed: {ex.Message}");
        }
    }

    private void StepWeaponFrame(int delta)
    {
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        var w = clip?.Weapon;
        if (w is null || w.Frames.Count == 0)
            return;
        int idx = w.Frames.FindIndex(f => f.SourceFrame == _weaponSourceFrame);
        if (idx < 0) idx = 0;
        idx = Math.Clamp(idx + delta, 0, w.Frames.Count - 1);
        _weaponSourceFrame = w.Frames[idx].SourceFrame;
        SeekToSourceFrame(clip!, _weaponSourceFrame);
        RefreshSequenceInspector();
    }

    private void SeekToSourceFrame(SequenceClip clip, int src)
    {
        var r = _seqResolved.FirstOrDefault(x => x.Clip.Id == clip.Id);
        if (r is null || _seqClip is null)
            return;
        for (int k = 0; k < r.OutputFrames; k++)
        {
            try
            {
                if (r.Map.SourceAt(k) == src)
                {
                    _engine.SeekFrame(r.StartFrame + k);
                    RefreshFrame();
                    return;
                }
            }
            catch { }
        }
    }

    private void ApplyWeaponPose(WeaponPose pose)
    {
        if (_seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip?.Weapon is null)
            return;
        var resolved = _seqResolved.FirstOrDefault(r => r.Clip.Id == clip.Id);
        int src = CurrentWeaponSourceFrame(clip, resolved);
        var frame = clip.Weapon.ForSource(src);
        if (frame is null)
        {
            ShowToast("Current source frame has no weapon pose — run Auto-detect first.");
            return;
        }
        var next = SequenceDocument.CloneWeapon(clip.Weapon)!;
        next.EnsureFrame(src).Pose = pose with { BehindBody = frame.Pose.BehindBody };
        _doc.SetClipWeapon(clip.Id, next);
        RebuildSequence();
        RefreshSequenceInspector();
        RefreshFrame();
    }

    private void SetWeaponFlag(bool? behind, bool? preserve, bool? nonBody, int? bodyColor)
    {
        if (_seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip?.Weapon is null)
            return;
        var next = SequenceDocument.CloneWeapon(clip.Weapon)!;
        if (behind.HasValue) next.BehindBody = behind.Value;
        if (preserve.HasValue) next.PreserveBody = preserve.Value;
        if (nonBody.HasValue) next.NonBodyMode = nonBody.Value;
        if (bodyColor.HasValue) next.BodyColorGreen = bodyColor.Value;
        _doc.SetClipWeapon(clip.Id, next);
        RebuildSequence();
    }

    private static double NormalizeDegrees(double d)
    {
        while (d > 180) d -= 360;
        while (d <= -180) d += 360;
        return d;
    }

    private void FlipWeapon(bool all)
    {
        if (_seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip?.Weapon is null || clip.Weapon.Frames.Count == 0)
        {
            ShowToast("Nothing to flip — run Auto-detect first.");
            return;
        }
        var next = SequenceDocument.CloneWeapon(clip.Weapon)!;
        if (all)
        {
            foreach (var f in next.Frames)
                f.Pose = f.Pose with { Degrees = NormalizeDegrees(f.Pose.Degrees + 180) };
        }
        else
        {
            var resolved = _seqResolved.FirstOrDefault(r => r.Clip.Id == clip.Id);
            int src = CurrentWeaponSourceFrame(clip, resolved);
            var frame = next.ForSource(src);
            if (frame is null)
            {
                ShowToast("Current source frame has no weapon pose — run Auto-detect first.");
                return;
            }
            frame.Pose = frame.Pose with { Degrees = NormalizeDegrees(frame.Pose.Degrees + 180) };
        }
        _doc.SetClipWeapon(clip.Id, next);
        RebuildSequence();
        RefreshSequenceInspector();
        RefreshFrame();
    }

    private void RefreshDonorGallery()
    {
        if (_loader is null)
            return;
        var attacks = _loader.Catalog.Animations
            .Where(a => AnimationCategorizer.Categorize(a.Id) == AnimationCategory.Attacks)
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => (a.Id, a.FrameCount))
            .ToList();
        SeqInspector.SetDonorGallery(attacks);
        SeqInspector.ForEachDonorItem(item =>
        {
            if (_thumbs is not null && item.Thumbnail is null)
                item.Thumbnail = _thumbs.TryGet(item.Id);
        });
    }

    private void OnDonorHover(string? donorId)
    {
        if (string.IsNullOrEmpty(donorId))
        {
            ClearDonorHover();
            return;
        }
        // Don't fight playback: hover preview is a paused-editing aid.
        if (_engine.IsPlaying || !_seqMode || _loader is null)
            return;
        if (_seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip is null)
            return;
        if (_donorHoverThrottle.IsRunning && _donorHoverThrottle.ElapsedMilliseconds < 120 &&
            string.Equals(_donorHoverId, donorId, StringComparison.Ordinal))
            return;
        _donorHoverThrottle.Restart();
        try
        {
            var resolved = _seqResolved.FirstOrDefault(r => r.Clip.Id == clip.Id);
            int src = CurrentWeaponSourceFrame(clip, resolved);
            int donorFrame = Math.Max(0, SeqInspector.WeaponDonorFrameValue);
            int donorCount = SourceCountOrMinusOne(donorId);
            if (donorCount <= 0)
                return;
            var bodyF = _nativeCache.Get(_loader, clip.AnimationId, src, _doc.Project.Fps);
            var donorF = _nativeCache.Get(_loader, donorId, Math.Clamp(donorFrame, 0, donorCount - 1), _doc.Project.Fps);
            var bodyImg = new RgbaImage(bodyF.Width, bodyF.Height, (byte[])bodyF.Rgba.Clone());
            var donorImg = new RgbaImage(donorF.Width, donorF.Height, (byte[])donorF.Rgba.Clone());
            bool nonBody = SeqInspector.WeaponNonBodyValue;
            int bodyColor = SeqInspector.WeaponBodyColorValue;
            int bodyGreen = SeqInspector.WeaponBodyGreenValue;
            int donorGreen = SeqInspector.WeaponDonorGreenValue;
            var shaft = nonBody ? NonBodyPoints(bodyImg, bodyColor) : BandPoints(bodyImg, bodyGreen);
            var dpoints = nonBody ? NonBodyPoints(donorImg, bodyColor) : BandPoints(donorImg, donorGreen);
            if (shaft.Count == 0 || dpoints.Count == 0)
                return;
            var pose = EstimateHoverPose(shaft, dpoints);
            if (pose is null)
                return;
            var padded = new RgbaImage(bodyImg.Width, bodyImg.Height, new byte[bodyImg.Width * bodyImg.Height * 4]);
            int cw = Math.Min(donorImg.Width, bodyImg.Width), ch = Math.Min(donorImg.Height, bodyImg.Height);
            for (int y = 0; y < ch; y++)
                Array.Copy(donorImg.Pixels, y * donorImg.Width * 4, padded.Pixels, y * bodyImg.Width * 4, cw * 4);
            var appearance = new RgbaImage(bodyImg.Width, bodyImg.Height, new byte[bodyImg.Width * bodyImg.Height * 4]);
            WeaponComposition.Restore(appearance, padded, WeaponPalette.ToRuns(dpoints));
            var opaque = WeaponPalette.OpaqueRuns(bodyImg.Pixels, bodyImg.Width, bodyImg.Height);
            var foreground = WeaponPalette.Subtract(opaque, shaft);
            var composed = WeaponComposition.Compose(bodyImg, WeaponPalette.ToRuns(shaft),
                appearance, pose.Value.PivotX, pose.Value.PivotY, pose.Value.Pose, foreground);
            var bmp = FrameBitmap.FromRgba(composed.Pixels, composed.Width, composed.Height);
            _donorHoverBmp?.Dispose();
            _donorHoverBmp = bmp;
            _donorHoverId = donorId;
            Viewport.SetFrame(bmp);
        }
        catch
        {
            // Hover preview must never break editing; fall back to committed frame.
        }
    }

    private void ClearDonorHover()
    {
        _donorHoverId = null;
        if (_donorHoverBmp is not null)
        {
            _donorHoverBmp.Dispose();
            _donorHoverBmp = null;
            if (_seqMode)
                RefreshFrame();
        }
    }

    private static HashSet<(int X, int Y)> BandPoints(RgbaImage img, int green)
    {
        var set = new HashSet<(int X, int Y)>();
        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            int i = (y * img.Width + x) * 4;
            if (img.Pixels[i + 3] == 0) continue;
            if (img.Pixels[i + 1] == green) set.Add((x, y));
        }
        return set;
    }

    private static HashSet<(int X, int Y)> NonBodyPoints(RgbaImage img, int bodyGreen)
    {
        var set = new HashSet<(int X, int Y)>();
        for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            int i = (y * img.Width + x) * 4;
            if (img.Pixels[i + 3] == 0) continue;
            if (img.Pixels[i + 1] != bodyGreen) set.Add((x, y));
        }
        return set;
    }

    private static (double PivotX, double PivotY, WeaponPose Pose)? EstimateHoverPose(
        HashSet<(int X, int Y)> shaft, HashSet<(int X, int Y)> donor)
    {
        try
        {
            var (sDeg, sCx, sCy, _) = WeaponPalette.ShaftAxis(shaft);
            var (dDeg, dCx, dCy, _) = WeaponPalette.ShaftAxis(donor);
            var pose = WeaponPalette.EstimatePose(shaft, (sDeg, sCx, sCy), donor, (dDeg, dCx, dCy), (dCx, dCy));
            return (dCx, dCy, pose);
        }
        catch
        {
            // Blob-like weapons with no shaft elongation: center the donor on the shaft centroid.
            if (shaft.Count == 0 || donor.Count == 0)
                return null;
            double sx = shaft.Average(p => p.X + 0.5), sy = shaft.Average(p => p.Y + 0.5);
            double dx = donor.Average(p => p.X + 0.5), dy = donor.Average(p => p.Y + 0.5);
            return (dx, dy, new WeaponPose(sx, sy, 0));
        }
    }

    private async Task ImportWeaponPlanAsync()
    {
        if (_seqTarget != SeqTargetKind.Clip)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        if (clip is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import weapon plan JSON",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Weapon plan") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0)
            return;
        try
        {
            var (body, weapon, _, _, _) = WeaponPlanIO.Import(await File.ReadAllTextAsync(files[0].Path.LocalPath));
            if (!string.Equals(body, clip.AnimationId, StringComparison.Ordinal))
            {
                ShowToast($"Plan body '{body}' does not match clip '{clip.AnimationId}'.");
                return;
            }
            _doc.SetClipWeapon(clip.Id, weapon);
            _weaponSourceFrame = weapon.Frames[0].SourceFrame;
            RebuildSequence();
            RefreshSequenceInspector();
            RefreshFrame();
        }
        catch (Exception ex)
        {
            ShowToast($"Weapon import failed: {ex.Message}");
        }
    }

    private async Task ExportWeaponPlanAsync()
    {
        if (_seqTarget != SeqTargetKind.Clip || _loader is null)
            return;
        var clip = _doc.Project.Clips.FirstOrDefault(c => c.Id == _seqTargetId);
        var w = clip?.Weapon;
        if (clip is null || w is null || w.Frames.Count == 0)
        {
            ShowToast("Nothing to export — run Auto-detect first.");
            return;
        }
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
            return;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export BTD-compatible weapon plan",
            SuggestedFileName = $"{clip.AnimationId}-weapon.json",
            FileTypeChoices = new[] { new FilePickerFileType("Weapon plan") { Patterns = new[] { "*.json" } } },
        });
        if (file is null)
            return;
        try
        {
            var body0 = _nativeCache.Get(_loader, clip.AnimationId, w.Frames[0].SourceFrame, _doc.Project.Fps);
            RgbaImage Resolve(string anim, int frame)
            {
                var f = _nativeCache.Get(_loader, anim, frame, _doc.Project.Fps);
                if (f.Width == body0.Width && f.Height == body0.Height)
                    return new RgbaImage(f.Width, f.Height, (byte[])f.Rgba.Clone());
                // Pad smaller donor canvases top-left (BTD convention).
                var padded = new RgbaImage(body0.Width, body0.Height, new byte[body0.Width * body0.Height * 4]);
                int cw = Math.Min(f.Width, body0.Width), ch = Math.Min(f.Height, body0.Height);
                for (int y = 0; y < ch; y++)
                    Array.Copy(f.Rgba, y * f.Width * 4, padded.Pixels, y * body0.Width * 4, cw * 4);
                if (anim == clip.AnimationId && (f.Width != body0.Width || f.Height != body0.Height))
                    throw new InvalidDataException("Mixed body canvas sizes are not exportable.");
                return padded;
            }
            string json = WeaponPlanIO.Export($"{clip.AnimationId}-studio", clip.AnimationId,
                body0.Width, body0.Height, w, Resolve);
            await File.WriteAllTextAsync(file.Path.LocalPath, json);
        }
        catch (Exception ex)
        {
            ShowToast($"Weapon export failed: {ex.Message}");
        }
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
        _clipboard.Clear();
        ClearFrameSelection();
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
            ClearFrameSelection();
            _clipboard.Clear();
            int clamped = ProjectSerializer.ClampFrameRanges(_doc.Project,
                id => _loader.Catalog.GetFrameRegions(id).Count);
            _seqSelectedClip = null;
            SetSequenceMode(true);
            RebuildSequence();
            var notes = new List<string>();
            if (missing.MissingAnimationIds.Count > 0)
                notes.Add("missing animations: " + string.Join(", ", missing.MissingAnimationIds.Take(5)));
            if (clamped > 0)
                notes.Add($"trimmed {clamped} clip(s) to the available frames (game version drift)");
            if (notes.Count > 0)
                ShowToast("Project references changed since it was saved (check game version): " +
                    string.Join("; ", notes));
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

    private async Task MaybeRunAuditAsync()
    {
        // Check before try: the finally below shuts the app down, and it must
        // only ever run for real --audit invocations, never normal launches.
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--audit");
        if (i < 0 || i + 1 >= args.Length)
            return;
        try
        {
            string dir = args[i + 1];
            await Task.Delay(800); // let auto-load + layout settle
            if (_loader is null)
                throw new InvalidOperationException("No game source loaded; cannot audit.");
            await RunAuditAsync(dir);
        }
        catch (Exception ex)
        {
            AuditLog($"[FATAL] audit runner: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
        finally
        {
            try
            {
                File.AppendAllText(Path.Combine(_auditDir, "AUDIT_LOG.txt"),
                    $"AS-M4 audit finished: {_auditPass} passed, {_auditFail} failed{Environment.NewLine}");
                Environment.ExitCode = _auditFail > 0 ? 1 : 0;
            }
            catch { }
            await Task.Delay(300);
            Close();
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
