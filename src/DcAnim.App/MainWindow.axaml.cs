using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DcAnim.Core.Catalog;
using DcAnim.Core.Model;
using DcAnim.Core.Playback;
using DcAnim.Core.Services;
using DcAnim.Core.Sources;
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

    public MainWindow()
    {
        InitializeComponent();
        ApplySettings();
        ApplyEvidenceSize();

        Browser.AnimationSelected += SelectAnimation;
        Transport.PlayPauseRequested += TogglePlay;
        Transport.FirstRequested += () => { _engine.SeekFrame(0); RefreshFrame(); };
        Transport.PreviousRequested += () => { _engine.StepPrevious(); RefreshFrame(); };
        Transport.NextRequested += () => { _engine.StepNext(); RefreshFrame(); };
        Transport.LastRequested += () =>
        {
            if (_clip is not null) { _engine.SeekFrame(_clip.FrameCount - 1); RefreshFrame(); }
        };
        Transport.SpeedChanged += s => _engine.Speed = s;
        Transport.FpsChanged += ApplyFps;
        Transport.LoopChanged += loop => _engine.Loop = loop;
        Viewport.FitRequested += FitPreview;
        Timeline.ScrubRequested += f => { _engine.Scrub(f); RefreshFrame(); };

        OpenPakItem.Click += async (_, _) => await OpenPakAsync();
        OpenAtlasItem.Click += async (_, _) => await OpenAtlasFolderAsync();
        ReloadItem.Click += (_, _) => Reload();
        ExitItem.Click += (_, _) => Close();
        PlayPauseItem.Click += (_, _) => TogglePlay();
        PrevFrameItem.Click += (_, _) => { _engine.StepPrevious(); RefreshFrame(); };
        NextFrameItem.Click += (_, _) => { _engine.StepNext(); RefreshFrame(); };
        FirstFrameItem.Click += (_, _) => { _engine.SeekFrame(0); RefreshFrame(); };
        LastFrameItem.Click += (_, _) =>
        {
            if (_clip is not null) { _engine.SeekFrame(_clip.FrameCount - 1); RefreshFrame(); }
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
        Closing += (_, _) =>
        {
            if (!IsEvidenceRun())
                SaveSettings();
        };

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

    private void TogglePlay()
    {
        if (_clip is null)
            return;
        if (_engine.IsPlaying)
            _engine.Pause();
        else
            _engine.Play();
        _clock.Restart();
        _lastTickSeconds = 0;
        RefreshFrame();
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
        if (_clip is null || _clip.FrameCount == 0)
            return;
        Viewport.Preview.Pan = new Avalonia.Vector(0, 0);
        Viewport.FitToView(_clip.Frames[0].Width, _clip.Frames[0].Height);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_clip is null || !_engine.IsPlaying)
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
    }

    private void RefreshFrame()
    {
        if (_clip is null)
        {
            Viewport.SetFrame(null);
            Transport.SetPlaying(false);
            return;
        }
        var frame = _engine.CurrentFrame;
        if (frame is not null && _frameCache.TryGetValue(frame.RegionName, out var bmp))
            Viewport.SetFrame(bmp);
        Timeline.SetPosition(_engine.CurrentFrameIndex, _clip.FrameCount, _fps);
        Inspector.SetPosition(_engine.CurrentFrameIndex, _clip.FrameCount, frame?.RegionName ?? "—");
        Transport.SetPlaying(_engine.IsPlaying);
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
            ("Ctrl+F", "Focus animation search"),
            ("Ctrl+0", "Fit preview to view"),
            ("Ctrl+B", "Toggle checkerboard background"),
            ("Ctrl+O", "Open res.pak"),
            ("Ctrl + wheel", "Zoom timeline (over track)"),
            ("F5", "Reload source"),
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
                case Key.O:
                    _ = OpenPakAsync();
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
            case Key.Space when !onButton:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Left:
                _engine.StepPrevious();
                RefreshFrame();
                e.Handled = true;
                break;
            case Key.Right:
                _engine.StepNext();
                RefreshFrame();
                e.Handled = true;
                break;
            case Key.Home:
                _engine.SeekFrame(0);
                RefreshFrame();
                e.Handled = true;
                break;
            case Key.End:
                if (_clip is not null)
                {
                    _engine.SeekFrame(_clip.FrameCount - 1);
                    RefreshFrame();
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

    // ---------- evidence mode ----------

    private static bool IsEvidenceRun() =>
        Array.IndexOf(Environment.GetCommandLineArgs(), "--evidence") >= 0;

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
