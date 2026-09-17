using System.Collections;
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
    Console.WriteLine("Opt-in anonymous production-chain check: --live [--full] [--video VIDEO_ID]. Volume is always zero.");
    return;
}
int videoOption = Array.IndexOf(args, "--video");
string videoId = videoOption < 0 ? "QoXDQa9L12A" : args.ElementAtOrDefault(videoOption + 1) ?? "";
if (!Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$"))
{
    Console.WriteLine("Invalid video ID."); Environment.ExitCode = 2; return;
}
// An explicit, unique store never touches the user's LocalAppData account.
string temporary = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "ytmusicwinui-proof-smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(12));
DispatcherQueueController? controller = null;
try
{
    controller = DispatcherQueueController.CreateOnDedicatedThread();
    var dispatcher = controller.DispatcherQueue;
    Require(dispatcher.TryEnqueue(async () =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
        CoreService? core = null;
        PlaybackService? playback = null;
        int outcome = 0, forceCalls = 0, proactiveCalls = 0, completedProofs = 0, ownWorkerId = 0;
        bool wrongThread = false;
        try
        {
            Console.WriteLine("production_core_create: started");
            core = await CoreService.CreateAsync(lifetime.Token, "en-US", new SessionStore(temporary));
            Console.WriteLine("production_core_create: completed");
            Require(!core.IsAuthenticated && core.SessionStatus == "signed_out", "anonymous_store_required");
            async Task EnsureProof(string id, bool force, CancellationToken cancellation)
            {
                if (force) Interlocked.Increment(ref forceCalls); else Interlocked.Increment(ref proactiveCalls);
                Console.WriteLine($"proof_callback: started force={force}");
                await core.EnsurePlaybackProofAsync(id, force, cancellation);
                Console.WriteLine($"proof_callback: completed force={force}");
                Interlocked.Increment(ref completedProofs);
                ownWorkerId = WorkerId(core);
            }
            playback = new PlaybackService(core, dispatcher, key => key, EnsureProof) { Volume = 0 };
            playback.PropertyChanged += (_, _) => wrongThread |= !dispatcher.HasThreadAccess;
            var player = Field<MediaPlayer>(playback, "player");
            var controls = Field<SystemMediaTransportControls>(playback, "systemControls");
            player.MediaFailed += (_, error) => Console.WriteLine($"native_media_failure: hresult=0x{error.ExtendedErrorCode.HResult:X8}");
            var first = new MusicItem { VideoId = videoId, Title = "Silent proof recovery check" };
            var replacement = new MusicItem { VideoId = videoId, Title = "Same-video cache check" };
            try
            {
                await core.CallAsync(new { op = "library", section = "songs" }, lifetime.Token);
                throw new ProbeFailure("anonymous_library_unexpectedly_allowed");
            }
            catch (MusicCoreException error) when (error.Code == "authentication_required") { }
            Require(!core.IsAuthenticated && core.SessionStatus == "signed_out", "login_requirement_poisoned_anonymous_state");
            Console.WriteLine("anonymous_library_requires_login_without_rejecting_session: passed");
            Console.WriteLine("initial_track_load: started");
            await playback.PlayAsync(first, [first]);
            Console.WriteLine("initial_track_load: returned");
            await SustainedClock(playback, () => Volatile.Read(ref completedProofs) > 0, 15, lifetime.Token);
            Require(forceCalls == 1 && completedProofs >= 1, "automatic_proof_recovery_not_observed");
            Require(controls.PlaybackStatus == MediaPlaybackStatus.Playing, "smtc_playing");
            Console.WriteLine($"proof_recovery_clock: passed forced={forceCalls} proactive={proactiveCalls} position_seconds={playback.PositionSeconds:F2}");

            if (args.Contains("--full"))
            {
                // Subscribe only after the replacement source has played continuously for 15 seconds.
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.MediaEnded += (_, _) => ended.TrySetResult();
                double remaining = playback.DurationSeconds - playback.PositionSeconds;
                Require(remaining > 5, "full_track_remaining_too_short");
                int forceAtStart = forceCalls;
                var wall = Stopwatch.StartNew();
                Task naturalEnd = ended.Task.WaitAsync(TimeSpan.FromSeconds(remaining + 60), lifetime.Token);
                while (!naturalEnd.IsCompleted)
                {
                    await Task.WhenAny(naturalEnd, Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token));
                    Console.WriteLine($"full_track_progress: elapsed_seconds={wall.Elapsed.TotalSeconds:F1} position_seconds={playback.PositionSeconds:F1}");
                }
                await naturalEnd;
                Require(wall.Elapsed.TotalSeconds >= remaining - 2 && forceCalls == forceAtStart, "premature_or_replaced_natural_end");
                Console.WriteLine($"natural_end_after_recovery: passed expected_seconds={remaining:F2} wall_seconds={wall.Elapsed.TotalSeconds:F2}");
            }
            else
            {
                double target = playback.DurationSeconds * .6;
                playback.Seek(target);
                await Until(() => playback.IsPlaying && playback.PositionSeconds > target + 2, 40, lifetime.Token);
                playback.TogglePlayPause();
                await Until(() => !playback.IsPlaying && controls.PlaybackStatus == MediaPlaybackStatus.Paused, 10, lifetime.Token);
                Console.WriteLine("midtrack_seek_and_pause: passed");
            }

            object priorProof = ProofEntry(core, videoId);
            int priorWorker = WorkerId(core), priorForce = forceCalls, priorProactive = proactiveCalls;
            await playback.PlayAsync(replacement, [replacement]);
            await SustainedClock(playback, () => true, 6, lifetime.Token);
            Require(proactiveCalls > priorProactive && forceCalls == priorForce, "new_selection_proof_callback");
            Require(ReferenceEquals(priorProof, ProofEntry(core, videoId)) && WorkerId(core) == priorWorker, "same_video_proof_cache_not_reused");
            Console.WriteLine("same_video_proactive_callback_and_cached_proof: passed");

            var switches = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                switches.Add(playback.PlayAsync(i % 2 == 0 ? first : replacement));
                await Task.Delay(25, lifetime.Token);
            }
            await Task.WhenAll(switches).WaitAsync(TimeSpan.FromSeconds(90), lifetime.Token);
            await SustainedClock(playback, () => true, 6, lifetime.Token);
            Require(ReferenceEquals(playback.Current, first), "stale_selection_won");
            Task inFlight = playback.PlayAsync(replacement);
            await playback.StopAsync().WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            await inFlight.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            Require(playback.Current is null && playback.Queue.Count == 0 && !playback.IsBusy && !playback.IsPlaying, "stop_drain_state");
            Require(!wrongThread, "property_change_outside_dispatcher");
            Require(!Directory.EnumerateFiles(temporary).Any(), "anonymous_test_persisted_session");
            Console.WriteLine("rapid_replacement_cancel_drain_dispatcher: passed");
        }
        catch (Exception error)
        {
            outcome = 1;
            Console.WriteLine(error switch
            {
                ProbeFailure check => "check_failed:" + check.Message,
                MusicCoreException music => "core_error:" + SafeCode(music.Code),
                _ => $"runtime_error:{error.GetType().Name}:0x{error.HResult:X8}"
            });
            if (playback is not null) Console.WriteLine("status_key:" + SafeCode(playback.StatusText));
        }
        finally
        {
            try
            {
                if (core is not null && WorkerId(core) is int activeWorker && activeWorker != 0) ownWorkerId = activeWorker;
                if (playback is not null) await playback.DisposeAsync();
                if (core is not null) await core.DisposeAsync();
                Require(ownWorkerId == 0 || ProcessExited(ownWorkerId), "owned_worker_survived_disposal");
                Console.WriteLine("production_dispose_and_owned_worker_exit: passed");
            }
            catch (Exception) { outcome = 1; Console.WriteLine("cleanup_failed"); }
            done.TrySetResult(outcome);
        }
    }), "dispatcher_unavailable");
    Environment.ExitCode = await done.Task.WaitAsync(lifetime.Token);
}
catch (Exception error) { Console.WriteLine("harness_error:" + error.GetType().Name); Environment.ExitCode = 1; lifetime.Cancel(); }
finally
{
    if (controller is not null) await controller.ShutdownQueueAsync();
    try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

static T Field<T>(object value, string name) => (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!);
static object ProofEntry(CoreService core, string videoId) => Field<IDictionary>(core, "playbackProofs")[videoId] ?? throw new ProbeFailure("proof_cache_entry_missing");
static int WorkerId(CoreService core)
{
    var provider = Field<object>(core, "proofProvider");
    var process = (Process?)provider.GetType().GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider);
    return process is { HasExited: false } ? process.Id : 0;
}
static bool ProcessExited(int id)
{
    try { using var process = Process.GetProcessById(id); return process.HasExited; }
    catch (ArgumentException) { return true; }
}
static string SafeCode(string value) => value.Length <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : "redacted";
static void Require(bool condition, string code) { if (!condition) throw new ProbeFailure(code); }
static async Task Until(Func<bool> condition, int seconds, CancellationToken cancellation)
{
    var wall = Stopwatch.StartNew();
    while (!condition())
    {
        if (wall.Elapsed.TotalSeconds > seconds) throw new ProbeFailure("wait_timeout");
        await Task.Delay(100, cancellation);
    }
}
static async Task SustainedClock(PlaybackService playback, Func<bool> prerequisite, int seconds, CancellationToken cancellation)
{
    var wall = Stopwatch.StartNew();
    double? startPosition = null;
    TimeSpan stableSince = default;
    while (wall.Elapsed.TotalSeconds < 150)
    {
        if (prerequisite() && playback.IsPlaying && !playback.IsBusy && playback.DurationSeconds > 20)
        {
            if (startPosition is null || playback.PositionSeconds < startPosition)
            { startPosition = playback.PositionSeconds; stableSince = wall.Elapsed; }
            if ((wall.Elapsed - stableSince).TotalSeconds > seconds && playback.PositionSeconds > startPosition + seconds) return;
        }
        else startPosition = null;
        await Task.Delay(100, cancellation);
    }
    throw new ProbeFailure("sustained_clock_timeout");
}
sealed class ProbeFailure(string code) : Exception(code);
