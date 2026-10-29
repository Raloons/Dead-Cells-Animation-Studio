using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using DcAnim.Core.Catalog;

namespace DcAnim.App.Views;

/// <summary>Library row view-model: ID plus async thumbnail.</summary>
public sealed class AnimationListItem : INotifyPropertyChanged
{
    private WriteableBitmap? _thumbnail;

    public required string Id { get; init; }
    public required int FrameCount { get; init; }
    public required AnimationCategory Category { get; init; }

    public string Meta => $"{FrameCount} frames";
    public string FrameMeta => $"{FrameCount}f";
    public string CategoryName => AnimationCategorizer.DisplayName(Category);
    public bool CategoryVisible => true;

    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Left asset library: search, category pills, list/grid views with lazy
/// thumbnails, selection. Emits <see cref="AnimationSelected"/>; the host
/// owns decoding and playback.
/// </summary>
public sealed partial class AssetBrowserView : UserControl
{
    private readonly List<AnimationListItem> _allItems = new();
    private readonly List<AnimationListItem> _filtered = new();
    private AnimationCategory? _category; // null = All
    private bool _gridMode;

    public event Action<string>? AnimationSelected;

    public string? SelectedId { get; private set; }

    public AssetBrowserView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        AnimationList.SelectionChanged += (_, _) => ForwardSelection(AnimationList.SelectedItem);
        AnimationGrid.SelectionChanged += (_, _) => ForwardSelection(AnimationGrid.SelectedItem);
        ListViewButton.IsCheckedChanged += (_, _) => { if (!_viewSyncing && ListViewButton.IsChecked == true) SetGridMode(false); };
        GridViewButton.IsCheckedChanged += (_, _) => { if (!_viewSyncing && GridViewButton.IsChecked == true) SetGridMode(true); };
        BuildCategoryPills();
    }

    public void SetCatalog(IReadOnlyList<AnimationListItem> items)
    {
        _allItems.Clear();
        _allItems.AddRange(items);
        UpdateCategoryCounts();
        ApplyFilter();
    }

    public void Clear()
    {
        _allItems.Clear();
        ApplyFilter();
    }

    /// <summary>
    /// Programmatic selection. Always routes through <see cref="RaiseSelected"/>
    /// exactly once so the host loads the animation — list events are
    /// suppressed during the sync to avoid double loads.
    /// </summary>
    public void Select(string id)
    {
        var item = _filtered.FirstOrDefault(i => i.Id == id);
        if (item is null)
            return;
        _syncing = true;
        try
        {
            AnimationList.SelectedItem = item;
            AnimationGrid.SelectedItem = item;
            if (!_gridMode)
                AnimationList.ScrollIntoView(item);
        }
        finally
        {
            _syncing = false;
        }
        RaiseSelected(id);
    }

    public void FocusSearch() => SearchBox.Focus();

    public void ForEachItem(Action<AnimationListItem> action)
    {
        foreach (var item in _allItems)
            action(item);
    }

    private void BuildCategoryPills()
    {
        CategoryPanel.Children.Clear();
        AddPill("All", null, isChecked: true);
        foreach (var c in new[] { AnimationCategory.Attacks, AnimationCategory.Movement, AnimationCategory.Idle, AnimationCategory.Other })
            AddPill(AnimationCategorizer.DisplayName(c), c, isChecked: false);
    }

    private void AddPill(string label, AnimationCategory? category, bool isChecked)
    {
        var pill = new ToggleButton
        {
            Content = label,
            Classes = { "ws-category" },
            IsChecked = isChecked,
            Tag = category,
        };
        pill.IsCheckedChanged += (_, _) =>
        {
            if (pill.IsChecked != true)
                return;
            foreach (var child in CategoryPanel.Children.OfType<ToggleButton>())
            {
                if (!ReferenceEquals(child, pill))
                    child.IsChecked = false;
            }
            _category = category;
            ApplyFilter();
        };
        // Prevent unchecking the active pill by re-checking.
        pill.Click += (_, _) =>
        {
            if (pill.IsChecked != true)
                pill.IsChecked = true;
        };
        CategoryPanel.Children.Add(pill);
    }

    private void UpdateCategoryCounts()
    {
        var counts = _allItems.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count());
        foreach (var pill in CategoryPanel.Children.OfType<ToggleButton>())
        {
            string base_ = pill.Tag is AnimationCategory c
                ? AnimationCategorizer.DisplayName(c)
                : "All";
            int n = pill.Tag is AnimationCategory cc
                ? counts.GetValueOrDefault(cc)
                : _allItems.Count;
            pill.Content = $"{base_} · {n}";
        }
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text ?? string.Empty;
        _filtered.Clear();
        foreach (var item in _allItems)
        {
            if (_category.HasValue && item.Category != _category.Value)
                continue;
            if (!string.IsNullOrWhiteSpace(query) &&
                !item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            _filtered.Add(item);
        }

        CountText.Text = $"{_filtered.Count} / {_allItems.Count} animations";
        bool hasItems = _filtered.Count > 0;
        AnimationList.ItemsSource = hasItems ? _filtered : null;
        AnimationGrid.ItemsSource = hasItems ? _filtered : null;
        AnimationList.IsVisible = !_gridMode && hasItems;
        AnimationGrid.IsVisible = _gridMode && hasItems;

        if (!hasItems)
        {
            EmptyText.IsVisible = true;
            EmptyText.Text = _allItems.Count == 0
                ? "No animation source loaded.\nUse File > Open res.pak… or open an extracted atlas folder."
                : "No animations match this filter.";
            StatusText.Text = string.Empty;
        }
        else
        {
            EmptyText.IsVisible = false;
        }

        // Visual sync only: the host owns loading through Select()/RaiseSelected.
        // Guarded assignment cannot trigger a load (ForwardSelection skips equality).
        var current = _filtered.FirstOrDefault(i => i.Id == SelectedId);
        AnimationList.SelectedItem = current;
        AnimationGrid.SelectedItem = current;
    }

    private bool _viewSyncing;

    private void SetGridMode(bool grid)
    {
        if (_viewSyncing)
            return;
        _viewSyncing = true;
        try
        {
            _gridMode = grid;
            ListViewButton.IsChecked = !grid;
            GridViewButton.IsChecked = grid;
            ApplyFilter();
        }
        finally
        {
            _viewSyncing = false;
        }
    }

    /// <summary>Evidence/testing hook for the list/grid view toggle.</summary>
    public void SetViewMode(bool grid) => SetGridMode(grid);

    private bool _syncing;

    private void ForwardSelection(object? selected)
    {
        if (_syncing)
            return;
        if (selected is AnimationListItem item && item.Id != SelectedId)
            RaiseSelected(item.Id);
    }

    private void RaiseSelected(string id)
    {
        SelectedId = id;
        // Keep both views in sync.
        if (AnimationList.SelectedItem is not AnimationListItem l || l.Id != id)
            AnimationList.SelectedItem = _filtered.FirstOrDefault(i => i.Id == id);
        if (AnimationGrid.SelectedItem is not AnimationListItem g || g.Id != id)
            AnimationGrid.SelectedItem = _filtered.FirstOrDefault(i => i.Id == id);
        AnimationSelected?.Invoke(id);
    }
}
