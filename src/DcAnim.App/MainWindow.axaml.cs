using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DcAnim.Core.Catalog;
using DcAnim.Core.Model;
using DcAnim.Core.Playback;
using DcAnim.Core.Sources;
using DcAnim.App.Controls;
using DcAnim.App.Services;

namespace DcAnim.App;

public sealed partial class MainWindow : Window
{
    private AnimationLoader? _loader;
    private AnimationClip? _clip;
    private readonly PlaybackEngine _engine = new();
    private readonly Dictionary<string, WriteableBitmap> _frameCache = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private double _lastTickSeconds;
    private string _preferredAnimation = "AtkKatanaB2";
    private double _fps = 30.0;

    public MainWindow()
    {
        InitializeComponent();

        AnimationList.SelectionChanged += (_, _) => SelectFromList();
        SearchBox.TextChanged += (_, _) => RefreshList();
        PlayButton.Click += (_, _) => TogglePlay();
        RestartButton.Click += (_, _) => { _engine.Restart(); RefreshFrame(); };
        PrevButton.Click += (_, _) => { _engine.StepPrevious(); RefreshFrame(); };
        NextButton.Click += (_, _) => { _engine.StepNext(); RefreshFrame(); };
        LoopCheck.IsCheckedChanged += (_, _) => _engine.Loop = LoopCheck.IsChecked ?? true;
        SpeedBox.SelectionChanged += (_, _) => ApplySpeed();
        FpsBox.SelectionChanged += (_, _) => ApplyFps();
        ZoomSlider.ValueChanged += (_, _) => Preview.Zoom = ZoomSlider.Value;
        Timeline.ScrubRequested += f => { _engine.Scrub(f); RefreshFrame(); };

        OpenPakItem.Click += async (_, _) => await OpenPakAsync();
        OpenAtlasItem.Click += async (_, _) => await OpenAtlasFolderAsync();
        ReloadItem.Click += (_, _) => Reload();
        ExitItem.Click += (_, _) => Close();
        AboutItem.Click += async (_, _) => await ShowAboutAsync();

        _engine.FrameChanged += () => Dispatcher.UIThread.Post(RefreshFrame);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        Preview.Zoom = ZoomSlider.Value;

        Loaded += (_, _) => AutoLoad();
        Opened += (_, _) => _ = MaybeRunEvidenceModeAsync();
    }

    /// <summary>
    /// Headless-friendly evidence capture: <c>--evidence &lt;dir&gt;</c> loads the
    /// default animation, steps to a mid frame and saves a window screenshot plus
    /// the raw decoded frame PNGs, then exits. Used by docs/evidence generation.
    /// </summary>
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
            await Task.Delay(800); // let auto-load + layout settle
            if (_loader is null)
                throw new InvalidOperationException("No animation source loaded; cannot capture evidence.");
            if (!_loader.Catalog.Contains("AtkKatanaB2"))
                throw new InvalidOperationException("AtkKatanaB2 not in catalog; cannot capture evidence.");
            SelectAnimation("AtkKatanaB2");
            _engine.Scrub(0.45);
            RefreshFrame();
            await Task.Delay(400);

            var pixelSize = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            using (var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96)))
            {
                rtb.Render(this);
                rtb.Save(Path.Combine(dir, "app-mainwindow.png"));
            }

            if (_clip is not null)
            {
                foreach (var frame in _clip.Frames)
                {
                    using var bmp = FrameBitmap.ToWriteableBitmap(frame);
                    bmp.Save(Path.Combine(dir, $"{frame.RegionName}.png"));
                }
            }
            SetStatus($"Evidence saved to {dir}");
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
            SetStatus("No Dead Cells installation found. Use File > Open res.pak… or Open extracted atlas folder… . No game files are bundled with this tool.");
        }
        catch (Exception ex)
        {
            SetStatus($"Auto-detect failed: {ex.Message}");
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
        _loader = loader;
        _engine.Unload();
        _clip = null;
        ClearCache();
        if (prefer is not null)
            _preferredAnimation = prefer;
        RefreshList();
        SetStatus($"Loaded: {loader.SourceDescription} — {loader.Catalog.Count} animations.");
    }

    private void LoadFromResPak(string resPakPath, string atlasEntry = "atlas/beheaded.atlas")
    {
        try
        {
            var loader = AnimationLoader.FromResPak(resPakPath, atlasEntry, LoadTimingOverlay());
            SetLoader(loader);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load '{resPakPath}': {ex.Message}");
        }
    }

    private void LoadFromDirectory(string directory, string atlasFile = "beheaded.atlas")
    {
        try
        {
            var loader = AnimationLoader.FromDirectory(directory, atlasFile, LoadTimingOverlay());
            SetLoader(loader);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load '{directory}': {ex.Message}");
        }
    }

    private void Reload()
    {
        if (_loader is null)
        {
            AutoLoad();
            return;
        }
        string current = _clip?.Id ?? _preferredAnimation;
        // Rebuild from the same source description is not possible; re-auto-detect.
        AutoLoad();
        _preferredAnimation = current;
    }

    private void RefreshList()
    {
        if (_loader is null)
        {
            AnimationList.ItemsSource = null;
            CatalogCount.Text = string.Empty;
            return;
        }

        string query = SearchBox.Text ?? string.Empty;
        var items = _loader.Catalog.Search(query).Select(a => a.Id).ToList();
        AnimationList.ItemsSource = items;
        CatalogCount.Text = $"{items.Count} / {_loader.Catalog.Count} animations";

        string target = _clip?.Id ?? _preferredAnimation;
        int index = items.IndexOf(target);
        if (index < 0 && items.Count > 0)
            index = 0;
        if (index >= 0)
            AnimationList.SelectedIndex = index;
    }

    private void SelectFromList()
    {
        if (_loader is null || AnimationList.SelectedItem is not string id)
            return;
        SelectAnimation(id);
    }

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
            ApplySpeed();
            UpdateInspector();
            UpdateTimeline();
            RefreshFrame();
            _preferredAnimation = id;
            SetStatus($"Loaded: {_loader.SourceDescription} — {id} ({_clip.FrameCount} frames).");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load animation '{id}': {ex.Message}");
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
        PlayButton.Content = _engine.IsPlaying ? "⏸" : "▶";
        _clock.Restart();
        _lastTickSeconds = 0;
    }

    private void ApplySpeed()
    {
        if (SpeedBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), out double s))
            _engine.Speed = s;
    }

    private void ApplyFps()
    {
        if (FpsBox.SelectedItem is ComboBoxItem item
            && double.TryParse(item.Tag?.ToString(), out double fps)
            && fps > 0)
        {
            _fps = fps;
            if (_clip is not null)
            {
                foreach (var f in _clip.Frames)
                    f.Duration = TimeSpan.FromSeconds(1.0 / _fps);
                UpdateInspector();
                UpdateTimeline();
                RefreshFrame();
            }
        }
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
            Preview.Frame = null;
            return;
        }
        var frame = _engine.CurrentFrame;
        if (frame is not null && _frameCache.TryGetValue(frame.RegionName, out var bmp))
            Preview.Frame = bmp;
        Timeline.CurrentFrame = _engine.CurrentFrameIndex;
        InspCurrent.Text = $"{_engine.CurrentFrameIndex + 1} / {_clip.FrameCount}  ({frame?.RegionName ?? "—"})";
        TimelineLabel.Text = $"Frame {_engine.CurrentFrameIndex + 1} / {_clip.FrameCount}";
        PlayButton.Content = _engine.IsPlaying ? "⏸" : "▶";
    }

    private void UpdateInspector()
    {
        if (_clip is null)
            return;
        InspId.Text = _clip.Id;
        InspFrames.Text = _clip.FrameCount.ToString();
        InspDuration.Text = $"{_clip.GetDuration().TotalSeconds:F3}";
        InspHit.Text = _clip.Timing?.HitFrame?.ToString() ?? "—";
        InspCharge.Text = _clip.Timing?.ChargeSeconds?.ToString("F2") ?? "—";
        InspFx.Text = _clip.Timing?.FxId ?? "—";
        InspTimingSrc.Text = _clip.Timing?.SourceNote ?? "Uniform 1/FPS (assumed; see docs).";
        InspSource.Text = _clip.SourceDescription;
    }

    private void UpdateTimeline()
    {
        if (_clip is null)
        {
            Timeline.FrameCount = 0;
            Timeline.CurrentFrame = -1;
            Timeline.HitFrame = -1;
            TimelineLabel.Text = "—";
            return;
        }
        Timeline.FrameCount = _clip.FrameCount;
        Timeline.CurrentFrame = _engine.CurrentFrameIndex;
        // Overlay hitFrame is stored as observed from CDB rows; the engine's
        // 0/1-based convention is not documented, so the marker is indicative.
        Timeline.HitFrame = _clip.Timing?.HitFrame ?? -1;
    }

    private void SetStatus(string message) => StatusText.Text = message;

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
            Width = 480,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Text = "Dead Cells Animation Studio (AS-M1)\n\n" +
                       "A standalone viewer for studying Dead Cells animations outside the game.\n\n" +
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
}
