using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Music.Desktop.Services;

/// <summary>Prefetches cursor pages near the viewport of an existing native scroll surface.</summary>
internal sealed class ScrollPrefetcher : IDisposable
{
    private sealed class Pending(FrameworkElement sentinel, Func<CancellationToken, Task<bool>> load)
    {
        public FrameworkElement Sentinel { get; } = sentinel;
        public Func<CancellationToken, Task<bool>> Load { get; } = load;
        public int Failures { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
    }

    private readonly ScrollViewer viewport;
    private readonly Action<Exception> onError;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration cancellationRegistration;
    private readonly DispatcherQueueTimer timer;
    private readonly List<Pending> pending = [];
    private bool running, requested, disposed;

    public ScrollPrefetcher(ScrollViewer viewport, CancellationToken cancellation, Action<Exception> onError)
    {
        this.viewport = viewport;
        this.onError = onError;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        token = lifetime.Token;
        timer = viewport.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = false;
        timer.Tick += Tick;
        viewport.ViewChanged += ViewChanged;
        viewport.SizeChanged += SizeChanged;
        viewport.LayoutUpdated += LayoutUpdated;
        cancellationRegistration = token.Register(() => viewport.DispatcherQueue.TryEnqueue(Dispose));
    }

    public void Add(FrameworkElement sentinel, Func<CancellationToken, Task<bool>> loadMore)
    {
        if (disposed || token.IsCancellationRequested) return;
        pending.Add(new Pending(sentinel, loadMore));
        Schedule();
    }

    private void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => Schedule();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Schedule();
    private void LayoutUpdated(object? sender, object args) => Schedule();

    private void Schedule()
    {
        if (disposed || token.IsCancellationRequested || pending.Count == 0) return;
        requested = true;
        if (!running && !timer.IsRunning) timer.Start();
    }

    private bool NearViewport(FrameworkElement sentinel)
    {
        if (!viewport.IsLoaded || !sentinel.IsLoaded || viewport.ViewportHeight <= 0) return false;
        try
        {
            double top = sentinel.TransformToVisual(viewport).TransformPoint(new Point()).Y;
            // Fetch one screen ahead, including cursors just passed during a fast scroll.
            return top >= -viewport.ViewportHeight && top <= viewport.ViewportHeight * 2;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private async void Tick(DispatcherQueueTimer sender, object args)
    {
        if (running || disposed || token.IsCancellationRequested) return;
        running = true;
        requested = false;
        try
        {
            foreach (var entry in pending.ToArray())
            {
                if (token.IsCancellationRequested) break;
                if (!NearViewport(entry.Sentinel)) continue;
                if (entry.RetryAfter > DateTimeOffset.UtcNow) { requested = true; continue; }
                try
                {
                    bool hasMore = await entry.Load(token);
                    token.ThrowIfCancellationRequested();
                    entry.Failures = 0;
                    entry.RetryAfter = default;
                    if (!hasMore)
                    {
                        pending.Remove(entry);
                        entry.Sentinel.Visibility = Visibility.Collapsed;
                    }
                    // Re-measure after the appended content has completed layout.
                    requested = true;
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (token.IsCancellationRequested) break;
                    if (++entry.Failures >= 3)
                    {
                        pending.Remove(entry);
                        entry.Sentinel.Visibility = Visibility.Collapsed;
                        onError(error);
                    }
                    else
                    {
                        entry.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(entry.Failures);
                        requested = true;
                    }
                    break;
                }
            }
        }
        finally
        {
            running = false;
            if (requested) Schedule();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Tick;
        viewport.ViewChanged -= ViewChanged;
        viewport.SizeChanged -= SizeChanged;
        viewport.LayoutUpdated -= LayoutUpdated;
        cancellationRegistration.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
        pending.Clear();
    }
}
