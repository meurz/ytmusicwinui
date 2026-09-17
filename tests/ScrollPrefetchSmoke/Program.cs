using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Music.Desktop.Services;
using Windows.Graphics;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        if (!arguments.Contains("--run"))
        {
            Console.WriteLine("Local native WinUI viewport checks: pass --run. No network or account is used.");
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(args =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new SmokeApplication();
        });
    }
}

internal sealed class SmokeApplication : Application
{
    private Window? window;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(90));

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new Window { Title = "ytmusicwinui isolated viewport checks" };
            window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, 480, 360));
            window.AppWindow.Show(false);
            await OffscreenAndScrollAsync();
            await ShortContentAndSerializationAsync();
            await CancellationAsync(dispose: false);
            await CancellationAsync(dispose: true);
            await TransientRetryAsync();
            await ExhaustedRetryAsync();
            Console.WriteLine("native_scroll_prefetch_checks: passed");
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            Console.WriteLine($"check_failed:{error.GetType().Name}:{error.Message}");
        }
        finally
        {
            lifetime.Cancel();
            window?.Close();
            Exit();
        }
    }

    private async Task<(ScrollViewer Viewport, Border Filler, Border Sentinel)> SceneAsync(double height)
    {
        var filler = new Border { Height = height };
        var sentinel = new Border { Height = 2 };
        var content = new StackPanel();
        content.Children.Add(filler);
        content.Children.Add(sentinel);
        var viewport = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window!.Content = viewport;
        await UntilAsync(() => viewport.IsLoaded && viewport.ViewportHeight > 100 && sentinel.IsLoaded);
        return (viewport, filler, sentinel);
    }

    private async Task OffscreenAndScrollAsync()
    {
        var scene = await SceneAsync(6000);
        int calls = 0;
        using var prefetch = new ScrollPrefetcher(scene.Viewport, lifetime.Token, Unexpected);
        prefetch.Add(scene.Sentinel, token => { token.ThrowIfCancellationRequested(); calls++; return Task.FromResult(false); });
        await PauseAsync(1000);
        Require(calls == 0, "distant_sentinel_fetched_eagerly");
        scene.Viewport.ChangeView(null, 6000 - scene.Viewport.ViewportHeight * 1.5, null, true);
        await UntilAsync(() => calls == 1);
        Require(6000 - scene.Viewport.VerticalOffset > scene.Viewport.ViewportHeight, "prefetch_started_only_after_sentinel_visible");
        await PauseAsync(700);
        Require(calls == 1, "completed_cursor_fetched_again");
        Console.WriteLine("distant_sentinel_and_native_scroll: passed");
    }

    private async Task ShortContentAndSerializationAsync()
    {
        var scene = await SceneAsync(10);
        int calls = 0, active = 0, maximum = 0;
        using var prefetch = new ScrollPrefetcher(scene.Viewport, lifetime.Token, Unexpected);
        prefetch.Add(scene.Sentinel, async token =>
        {
            maximum = Math.Max(maximum, ++active);
            try
            {
                await Task.Delay(350, token);
                token.ThrowIfCancellationRequested();
                scene.Filler.Height += 10;
                return ++calls < 4;
            }
            finally { active--; }
        });
        for (int i = 0; i < 30; i++)
        {
            scene.Viewport.Width = i % 2 == 0 ? 420 : 421;
            scene.Viewport.UpdateLayout();
            await PauseAsync(25);
        }
        await UntilAsync(() => calls == 4);
        await PauseAsync(700);
        Require(calls == 4 && maximum == 1 && active == 0, "short_content_or_serialization_failed");
        Console.WriteLine("short_content_cursor_completion_and_serial_signals: passed");
    }

    private async Task CancellationAsync(bool dispose)
    {
        var scene = await SceneAsync(10);
        using var page = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int calls = 0, mutations = 0, finished = 0;
        using var prefetch = new ScrollPrefetcher(scene.Viewport, page.Token, Unexpected);
        prefetch.Add(scene.Sentinel, async token =>
        {
            calls++;
            try
            {
                await Task.Delay(600, token);
                token.ThrowIfCancellationRequested();
                mutations++;
                return true;
            }
            finally { finished++; }
        });
        await UntilAsync(() => calls == 1);
        if (dispose) prefetch.Dispose(); else page.Cancel();
        scene.Viewport.Width = 410;
        scene.Viewport.UpdateLayout();
        await UntilAsync(() => finished == 1);
        await PauseAsync(800);
        Require(calls == 1 && mutations == 0, "cancellation_allowed_stale_mutation_or_reload");
        Console.WriteLine(dispose ? "dispose_cancels_inflight_and_detaches: passed" : "page_cancellation_discards_inflight: passed");
    }

    private async Task TransientRetryAsync()
    {
        var scene = await SceneAsync(10);
        int calls = 0, completed = 0;
        using var prefetch = new ScrollPrefetcher(scene.Viewport, lifetime.Token, _ => { });
        prefetch.Add(scene.Sentinel, token =>
        {
            token.ThrowIfCancellationRequested();
            if (++calls <= 2) throw new IOException("Expected transient test failure.");
            completed++;
            return Task.FromResult(false);
        });
        await UntilAsync(() => completed == 1, 20);
        await PauseAsync(700);
        Require(calls == 3, "transient_failure_did_not_retry_to_completion");
        Console.WriteLine("transient_failure_automatic_retry: passed");
    }

    private async Task ExhaustedRetryAsync()
    {
        var scene = await SceneAsync(10);
        int calls = 0, errors = 0;
        using var prefetch = new ScrollPrefetcher(scene.Viewport, lifetime.Token, _ => errors++);
        prefetch.Add(scene.Sentinel, token =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            throw new IOException("Expected persistent test failure.");
        });
        await UntilAsync(() => calls >= 3, 20);
        await PauseAsync(5000);
        for (int i = 0; i < 10; i++)
        {
            scene.Viewport.Width = i % 2 == 0 ? 420 : 421;
            scene.Viewport.UpdateLayout();
            await PauseAsync(50);
        }
        await PauseAsync(1500);
        Require(calls == 3 && errors == 1, "persistent_failure_retries_unbounded_or_unreported");
        Console.WriteLine("persistent_failure_retry_bound: passed");
    }

    private Task PauseAsync(int milliseconds) => Task.Delay(milliseconds, lifetime.Token);
    private async Task UntilAsync(Func<bool> predicate, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            Require(watch.Elapsed.TotalSeconds < seconds, "condition_timeout");
            await PauseAsync(25);
        }
    }
    private static void Require(bool condition, string category)
    {
        if (!condition) throw new InvalidOperationException(category);
    }
    private static void Unexpected(Exception error) => throw new InvalidOperationException("unexpected_prefetch_error", error);
}
