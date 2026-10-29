using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DcAnim.Core.Sources;

namespace DcAnim.App.Services;

/// <summary>
/// Lazily decodes first-frame library thumbnails in the background and caches
/// them. Decoding the full 572-animation catalog eagerly would cost hundreds
/// of megabytes; single-frame decode (<see cref="AnimationLoader.LoadFrame"/>)
/// keeps each thumbnail to one small bitmap.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly AnimationLoader _loader;
    private readonly Dictionary<string, WriteableBitmap> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _prefetchCts;
    private bool _disposed;

    public const int ThumbSize = 56;

    public event Action? ThumbnailsChanged;

    public ThumbnailService(AnimationLoader loader)
    {
        _loader = loader;
    }

    public int CachedCount
    {
        get { lock (_gate) return _cache.Count; }
    }

    public WriteableBitmap? TryGet(string animationId)
    {
        lock (_gate)
            return _cache.TryGetValue(animationId, out var bmp) ? bmp : null;
    }

    /// <summary>
    /// Ensures thumbnails for <paramref name="ids"/> in the background.
    /// Previous prefetches are cancelled; completion raises
    /// <see cref="ThumbnailsChanged"/> in batches.
    /// </summary>
    public void Prefetch(IEnumerable<string> ids)
    {
        ThrowIfDisposed();
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        var pending = ids.Where(id => TryGet(id) is null).Distinct(StringComparer.Ordinal).ToList();

        Task.Run(() =>
        {
            int done = 0;
            foreach (var id in pending)
            {
                if (cts.Token.IsCancellationRequested)
                    return;
                try
                {
                    var frame = _loader.LoadFrame(id, 0);
                    var thumb = FrameBitmap.ToThumbnail(frame, ThumbSize);
                    lock (_gate)
                    {
                        if (!_cache.ContainsKey(id))
                            _cache[id] = thumb;
                        else
                            thumb.Dispose();
                    }
                    if (++done % 48 == 0)
                        ThumbnailsChanged?.Invoke();
                }
                catch
                {
                    // A single bad region must not stall the library.
                }
            }
            ThumbnailsChanged?.Invoke();
        }, cts.Token);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        lock (_gate)
        {
            foreach (var bmp in _cache.Values)
                bmp.Dispose();
            _cache.Clear();
        }
    }
}
