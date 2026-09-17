using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Music.Desktop.Models;
using Music.Desktop.Services;
using Windows.Media;
using Windows.Media.Playback;
using YouTubeMusic.Interop;

if (!args.Contains("--live"))
{
    Console.WriteLine("Opt-in network test: --live [--full] [--opus] [--retry] [--video VIDEO_ID]. Always silent and anonymous.");
    return;
}
int videoArgument = Array.IndexOf(args, "--video");
string videoId = videoArgument < 0 ? "dQw4w9WgXcQ" : args.ElementAtOrDefault(videoArgument + 1) ?? "";
if (!Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$"))
{
    Console.WriteLine("Invalid video ID.");
    Environment.ExitCode = 2;
    return;
}
var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
DispatcherQueueController? controller = null;
try
{
    controller = DispatcherQueueController.CreateOnDedicatedThread();
    var dispatcher = controller.DispatcherQueue;
    dispatcher.TryEnqueue(async () =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
        PlaybackService? playback = null;
        CoreService? core = null;
        int outcome = 0;
        try
        {
            var native = await MusicCoreClient.CreateAsync("{\"language\":\"en\",\"country\":\"US\",\"playback_client\":\"web_remix\"}");
            core = new CoreService(native, args.Contains("--opus"), args.Contains("--retry"));
            playback = new PlaybackService(core, dispatcher) { Volume = 0 };
            bool wrongThread = false;
            playback.PropertyChanged += (_, _) => wrongThread |= !dispatcher.HasThreadAccess;
            // Inspect native state/events only. Never read or print the native Source/signed URI.
            var player = (MediaPlayer)typeof(PlaybackService).GetField("player", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(playback)!;
            var controls = (SystemMediaTransportControls)typeof(PlaybackService).GetField("systemControls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(playback)!;
            var first = new MusicItem { VideoId = videoId, Title = "Silent native playback check" };
            var replacement = new MusicItem { VideoId = videoId, Title = "Replacement source check" };
            if (args.Contains("--retry"))
            {
                await playback.PlayAsync(first);
                Require(playback.HasError && !playback.IsBusy && !playback.IsPlaying, "initial_failure_state");
                playback.TogglePlayPause();
                await StablePlayback(playback);
                Require(!playback.HasError, "retry_error_not_cleared");
                Console.WriteLine("play_button_retries_failed_source: passed");
                await playback.StopAsync();
            }
            await playback.PlayAsync(first, [first, replacement]);
            await StablePlayback(playback);
            Require(controls.PlaybackStatus == MediaPlaybackStatus.Playing, "smtc_playing");
            Console.WriteLine($"native_clock_and_smtc: passed duration_seconds={playback.DurationSeconds:F2}");
            if (args.Contains("--full"))
            {
                playback.Queue.Clear(); playback.Queue.Add(first);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.MediaEnded += (_, _) => ended.TrySetResult();
                double remaining = playback.DurationSeconds - playback.PositionSeconds;
                var wall = Stopwatch.StartNew();
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(remaining + 45));
                Require(wall.Elapsed.TotalSeconds >= remaining - 2, "premature_media_ended");
                Console.WriteLine($"full_natural_end: passed remaining_seconds={remaining:F2} wall_seconds={wall.Elapsed.TotalSeconds:F2}");
            }
            else
            {
                playback.Seek(playback.DurationSeconds / 2);
                await Until(() => playback.IsPlaying && playback.PositionSeconds >= playback.DurationSeconds / 2 + 1, 30);
                playback.TogglePlayPause();
                await Until(() => !playback.IsPlaying && controls.PlaybackStatus == MediaPlaybackStatus.Paused, 10);
                Console.WriteLine("seek_pause_smtc: passed");
                var switches = new List<Task>();
                for (int i = 0; i < 5; i++)
                {
                    switches.Add(playback.PlayAsync(i % 2 == 0 ? first : replacement));
                    await Task.Delay(25);
                }
                await Task.WhenAll(switches);
                await StablePlayback(playback);
                Require(ReferenceEquals(playback.Current, first), "stale_selection_won");
                Console.WriteLine("rapid_replacement: passed");
                Task inFlight = playback.PlayAsync(replacement);
                await playback.StopAsync();
                await inFlight;
                Require(playback.Current is null && playback.Queue.Count == 0 && !playback.IsBusy && !playback.IsPlaying, "stop_drain_state");
                await playback.PlayAsync(first);
                await StablePlayback(playback);
                Console.WriteLine("cancel_drain_and_reuse: passed");
            }
            Require(!wrongThread, "property_changed_outside_dispatcher");
            Console.WriteLine("dispatcher_notifications: passed");
        }
        catch (Exception error)
        {
            outcome = 1;
            Console.WriteLine(error switch
            {
                MusicCoreException music => "core_error:" + music.Code,
                ProbeFailure failure => "check_failed:" + failure.Message,
                _ => $"runtime_error:{error.GetType().Name}:0x{error.HResult:X8}"
            });
            if (playback is not null) Console.WriteLine("status:" + playback.StatusText);
        }
        finally
        {
            try
            {
                if (playback is not null) await playback.DisposeAsync();
                if (core is not null) await core.DisposeAsync();
                Console.WriteLine("clean_dispose: passed");
            }
            catch (Exception) { outcome = 1; Console.WriteLine("cleanup_failed"); }
            done.TrySetResult(outcome);
        }
    });
    Environment.ExitCode = await done.Task.WaitAsync(TimeSpan.FromMinutes(10));
}
catch (Exception error) { Console.WriteLine("harness_error:" + error.GetType().Name); Environment.ExitCode = 1; }
finally { if (controller is not null) await controller.ShutdownQueueAsync(); }

static void Require(bool condition, string code) { if (!condition) throw new ProbeFailure(code); }
static async Task Until(Func<bool> ready, int seconds)
{
    var elapsed = Stopwatch.StartNew();
    while (!ready())
    {
        if (elapsed.Elapsed.TotalSeconds > seconds) throw new ProbeFailure("wait_timeout");
        await Task.Delay(100);
    }
}
static async Task StablePlayback(PlaybackService playback)
{
    var elapsed = Stopwatch.StartNew();
    double? startPosition = null;
    TimeSpan since = default;
    while (elapsed.Elapsed.TotalSeconds < 100)
    {
        if (playback.IsPlaying && !playback.IsBusy && playback.DurationSeconds > 5)
        {
            if (startPosition is null || playback.PositionSeconds < startPosition)
            { startPosition = playback.PositionSeconds; since = elapsed.Elapsed; }
            if (elapsed.Elapsed - since > TimeSpan.FromSeconds(5) && playback.PositionSeconds > startPosition + 2) return;
        }
        else startPosition = null;
        await Task.Delay(100);
    }
    throw new ProbeFailure("sustained_playback_timeout");
}
sealed class ProbeFailure(string message) : Exception(message);
