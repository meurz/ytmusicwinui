using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Music.Desktop.Models;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Media.Core;
using YouTubeMusic.Interop;

namespace Music.Desktop.Services;

/// <summary>Native AAC/DASH playback with one Opus alternative. Signed URLs remain in memory only.</summary>
public sealed class PlaybackService : ObservableObject, IAsyncDisposable
{
    private readonly CoreService core;
    private readonly DispatcherQueue dispatcher;
    private readonly Func<string, string> localize;
    private readonly Func<string, bool, CancellationToken, Task>? ensurePo;
    private readonly MediaPlayer player = new() { AutoPlay = false };
    private readonly DispatcherQueueTimer timer;
    private readonly SystemMediaTransportControls systemControls;
    private readonly object tasksLock = new();
    private readonly HashSet<Task> pendingLoads = [];
    private WindowsMusicSource? source;
    private MediaSource? directSource;
    private bool usingWebm;
    private bool webmAttempted;
    private bool HasSource => source is not null || directSource is not null;
    private CancellationTokenSource? loadCancellation;
    private long generation;
    private bool disposed;
    private bool stopping;
    private bool closing;
    private bool recoveryAttempted;
    private bool proofRefreshAttempted;
    private bool playbackProofRequired;
    private double pendingSeek;
    private bool playWhenOpened;
    private MusicItem? current;
    private bool isPlaying;
    private bool isBusy;
    private bool hasError;
    private bool shuffle;
    private int repeatMode;
    private double positionSeconds;
    private double durationSeconds;
    private double volume = .75;
    private string statusText;

    public PlaybackService(CoreService core, DispatcherQueue dispatcher, Func<string, string>? localize = null,
        Func<string, bool, CancellationToken, Task>? ensurePo = null)
    {
        this.core = core;
        this.dispatcher = dispatcher;
        this.localize = localize ?? (static key => key);
        this.ensurePo = ensurePo;
        statusText = this.localize("ChooseTrackToStart");
        if (!dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Create playback on the window dispatcher thread.");
        player.Volume = volume;
        // MediaPlayer owns the desktop SMTC instance, so no CoreWindow/GetForCurrentView is required.
        player.CommandManager.IsEnabled = false;
        systemControls = player.SystemMediaTransportControls;
        systemControls.IsEnabled = true;
        systemControls.IsPlayEnabled = true;
        systemControls.IsPauseEnabled = true;
        systemControls.IsNextEnabled = true;
        systemControls.IsPreviousEnabled = true;
        systemControls.IsStopEnabled = true;
        systemControls.ButtonPressed += SystemButtonPressed;
        systemControls.PlaybackPositionChangeRequested += SystemSeekRequested;
        player.MediaOpened += MediaOpened;
        player.MediaEnded += MediaEnded;
        player.MediaFailed += MediaFailed;
        player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += TimerTick;
        timer.Start();
    }

    public ObservableCollection<MusicItem> Queue { get; } = [];
    public MusicItem? Current { get => current; private set => SetProperty(ref current, value); }
    public bool IsPlaying { get => isPlaying; private set => SetProperty(ref isPlaying, value); }
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }
    public bool HasError { get => hasError; private set => SetProperty(ref hasError, value); }
    public int NativeFailureCode { get; private set; }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }
    public double PositionSeconds { get => positionSeconds; private set => SetProperty(ref positionSeconds, value); }
    public double DurationSeconds { get => durationSeconds; private set => SetProperty(ref durationSeconds, value); }
    public bool Shuffle { get => shuffle; set => Post(() => SetProperty(ref shuffle, value)); }
    public int RepeatMode { get => repeatMode; set => Post(() => SetProperty(ref repeatMode, Math.Clamp(value, 0, 2))); }
    public double Volume
    {
        get => volume;
        set => Post(() =>
        {
            if (double.IsFinite(value) && SetProperty(ref volume, Math.Clamp(value, 0, 1)))
                player.Volume = volume;
        });
    }

    public Task PlayAsync(MusicItem item, IEnumerable<MusicItem>? context = null)
    {
        // Materialize on the caller/UI thread before asynchronous work starts.
        MusicItem[]? items = context?.Where(IsPlayable).ToArray();
        return Track(OnUiAsync(() =>
        {
            if (stopping || closing) return Task.CompletedTask;
            if (!IsPlayable(item))
            {
                SetStatus("ItemNotPlayable", error: true);
                return Task.CompletedTask;
            }
            if (items is not null)
            {
                Queue.Clear();
                foreach (MusicItem entry in items) Queue.Add(entry);
            }
            if (!Queue.Contains(item)) Queue.Add(item);
            return LoadAsync(item, false, 0, true);
        }));
    }

    public void TogglePlayPause() => Post(() =>
    {
        if (IsBusy) { playWhenOpened = !playWhenOpened; return; }
        if (!HasSource)
        {
            if (Current is not null) Observe(Track(LoadAsync(Current, false, 0, true)));
            return;
        }
        playWhenOpened = !IsPlaying;
        if (IsPlaying) player.Pause(); else player.Play();
    });

    public Task NextAsync() => Track(OnUiAsync(() => MoveAsync(1, false)));
    public Task PreviousAsync() => Track(OnUiAsync(() =>
    {
        if (PositionSeconds > 3 && HasSource) { Seek(0); return Task.CompletedTask; }
        return MoveAsync(-1, false);
    }));

    public void Enqueue(MusicItem item) => Post(() =>
    {
        if (IsPlayable(item)) { Queue.Add(item); SetStatus("AddedToQueue"); }
    });

    public void Seek(double seconds) => Post(() =>
    {
        if (!double.IsFinite(seconds)) return;
        double position = Math.Clamp(seconds, 0, DurationSeconds > 0 ? DurationSeconds : double.MaxValue);
        if (IsBusy) { pendingSeek = position; return; }
        if (!HasSource || !player.PlaybackSession.CanSeek) return;
        player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
        PositionSeconds = position;
        UpdateTimeline();
    });

    /// <summary>Cancel native requests before switching accounts or disposing the core.</summary>
    public async Task StopAsync()
    {
        await OnUiAsync(() =>
        {
            stopping = true;
            generation++;
            var cancellation = loadCancellation;
            loadCancellation = null;
            cancellation?.Cancel();
            DetachSource();
            Current = null;
            Queue.Clear();
            PositionSeconds = 0;
            DurationSeconds = 0;
            IsBusy = false;
            SetStatus("PlaybackStopped");
            systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            systemControls.DisplayUpdater.ClearAll();
            systemControls.DisplayUpdater.Update();
            return Task.CompletedTask;
        });
        Task[] pending;
        lock (tasksLock) pending = pendingLoads.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The caller observes failed commands; shutdown still releases native resources. */ }
        finally { await OnUiAsync(() => { stopping = closing; return Task.CompletedTask; }); }
    }

    private async Task LoadAsync(MusicItem item, bool refresh, double seek, bool autoPlay, bool webm = false, bool forceProof = false)
    {
        long requestGeneration = ++generation;
        loadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        DetachSource();
        Current = item;
        PositionSeconds = seek;
        DurationSeconds = 0;
        HasError = false;
        IsBusy = true;
        pendingSeek = seek;
        playWhenOpened = autoPlay;
        usingWebm = webm;
        bool newSelection = !refresh && !webm && !forceProof;
        if (newSelection)
        {
            recoveryAttempted = false;
            webmAttempted = false;
            proofRefreshAttempted = false;
        }
        bool prepareProof = ensurePo is not null && (forceProof || (newSelection && playbackProofRequired));
        NativeFailureCode = 0;
        SetStatus(prepareProof ? "VerifyingPlayback" : webm ? "TryingOpus" : refresh ? "RefreshingSource" : "PreparingAudio");
        WindowsMusicSource? replacement = null;
        MediaSource? directReplacement = null;
        bool resolvingSource = false;
        try
        {
            if (prepareProof)
            {
                // The host installs video-bound proof only after checking this cancellation token.
                // Keep preparation in the load task so source changes/account switches drain it.
                await ensurePo!(item.VideoId!, forceProof, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                await OnUiAsync(() =>
                {
                    if (requestGeneration == generation && !cancellation.IsCancellationRequested)
                    {
                        playbackProofRequired = true;
                        SetStatus(refresh ? "RefreshingSource" : webm ? "TryingOpus" : "PreparingAudio");
                    }
                    return Task.CompletedTask;
                });
                cancellation.Token.ThrowIfCancellationRequested();
            }
            resolvingSource = true;
            if (refresh)
                await core.CallAsync(new { op = "stream_refresh", video_id = item.VideoId, format = "mp4" }, cancellation.Token);
            if (webm)
            {
                // The core validates a fresh public CDN URL. No account cookies enter MediaPlayer.
                var stream = await core.CallAsync(new { op = "stream", video_id = item.VideoId, format = "webm" }, cancellation.Token);
                if (!Uri.TryCreate(stream.GetProperty("url").GetString(), UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))
                    throw new MusicCoreException("media_source", "Invalid public media URL");
                directReplacement = MediaSource.CreateFromUri(uri);
            }
            else
            {
                var manifest = await core.CallAsync(new { op = "dash_manifest", video_id = item.VideoId }, cancellation.Token);
                replacement = await WindowsMusicSource.FromManifestAsync(manifest, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                // Discard success racing with cancellation, account change or a newer selection.
                if (disposed || requestGeneration != generation || cancellation.IsCancellationRequested)
                    return Task.CompletedTask;
                source = replacement;
                directSource = directReplacement;
                replacement = null;
                directReplacement = null;
                player.Source = source?.Source ?? directSource;
                UpdateMetadata(item);
                SetStatus("LoadingAudio");
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() =>
            {
                if (!disposed && requestGeneration == generation)
                {
                    DetachSource();
                    SetStatus("AudioRequestTimeout", error: true);
                }
                return Task.CompletedTask;
            });
        }
        catch (MusicCoreException error) when (error.Code == "po_token_required")
        {
            await OnUiAsync(() =>
            {
                if (disposed || requestGeneration != generation) return Task.CompletedTask;
                Task? retry = StartProofRecovery(item, seek, autoPlay);
                if (retry is not null) return retry;
                HasError = true;
                StatusText = Describe(error);
                return Task.CompletedTask;
            });
        }
        catch (Exception error) when (resolvingSource && !webm &&
            (error is NotSupportedException || error is MusicCoreException { Code: "stream_unavailable" }))
        {
            await OnUiAsync(() =>
            {
                if (disposed || requestGeneration != generation || webmAttempted)
                    return Task.CompletedTask;
                webmAttempted = true;
                return LoadAsync(item, false, seek, autoPlay, webm: true);
            });
        }
        catch (Exception error)
        {
            await OnUiAsync(() =>
            {
                if (!disposed && requestGeneration == generation)
                {
                    DetachSource();
                    HasError = true;
                    StatusText = !resolvingSource && error is not MusicCoreException
                        ? localize("PlaybackVerificationFailed") : Describe(error);
                }
                return Task.CompletedTask;
            });
        }
        finally
        {
            replacement?.Dispose();
            directReplacement?.Dispose();
            await OnUiAsync(() =>
            {
                if (requestGeneration == generation)
                {
                    loadCancellation = null;
                    // MediaOpened clears the busy state after native initialization succeeds.
                    if (!HasSource) IsBusy = false;
                }
                return Task.CompletedTask;
            });
        }
    }

    private Task? StartProofRecovery(MusicItem item, double seek, bool autoPlay)
    {
        if (ensurePo is null || proofRefreshAttempted || stopping || closing) return null;
        proofRefreshAttempted = true;
        recoveryAttempted = true;
        // AAC may genuinely be unavailable on this host, so a fresh proved source can still
        // choose Opus once. Neither codec choice resets the proof-attempt counter.
        webmAttempted = false;
        return LoadAsync(item, true, seek, autoPlay, forceProof: true);
    }

    private Task MoveAsync(int direction, bool automatic)
    {
        if (stopping || closing || Queue.Count == 0) return Task.CompletedTask;
        if (automatic && RepeatMode == 2 && Current is not null)
            return LoadAsync(Current, false, 0, true);
        int index = Current is null ? -1 : Queue.IndexOf(Current);
        int next;
        if (Shuffle && direction > 0 && Queue.Count > 1)
        {
            next = Random.Shared.Next(index >= 0 ? Queue.Count - 1 : Queue.Count);
            if (next >= index && index >= 0) next++;
        }
        else next = index + direction;
        if (next >= Queue.Count || next < 0)
        {
            if (RepeatMode == 1) next = (next + Queue.Count) % Queue.Count;
            else
            {
                if (automatic) { player.Pause(); IsPlaying = false; SetStatus("QueueFinished"); }
                return Task.CompletedTask;
            }
        }
        return LoadAsync(Queue[next], false, 0, true);
    }

    private void MediaOpened(MediaPlayer sender, object args)
    {
        long observed = generation;
        Post(() =>
        {
            if (observed != generation || !HasSource) return;
            IsBusy = false;
            DurationSeconds = Math.Max(0, player.PlaybackSession.NaturalDuration.TotalSeconds);
            if (pendingSeek > 0 && player.PlaybackSession.CanSeek)
                player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Min(pendingSeek, Math.Max(0, DurationSeconds - .1)));
            pendingSeek = 0;
            if (playWhenOpened) player.Play();
            SetStatus(playWhenOpened ? "Playing" : "Paused");
            UpdateTimeline();
        });
    }

    private void MediaEnded(MediaPlayer sender, object args)
    {
        long observed = generation;
        Observe(Track(OnUiAsync(() => observed == generation && !IsBusy ? MoveAsync(1, true) : Task.CompletedTask)));
    }

    private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        long observed = generation;
        Observe(Track(OnUiAsync(() =>
        {
            if (stopping || closing || observed != generation || !HasSource || Current is null) return Task.CompletedTask;
            // Media Foundation can report CDN HTTP 401/403 as DecodingError.
            int failureCode = args.ExtendedErrorCode?.HResult ?? 0;
            NativeFailureCode = failureCode;
            // Windows can wrap an HTTP refusal as SourceNotSupported/NS_E_REFUSED_BY_SERVER.
            bool rejectedUrl = failureCode is unchecked((int)0x80190191) or unchecked((int)0x80190193)
                or unchecked((int)0xC00D2EE7);
            double resume = Math.Max(PositionSeconds, player.PlaybackSession.Position.TotalSeconds);
            bool resumePlaying = IsPlaying || playWhenOpened;
            if (rejectedUrl)
            {
                Task? proofRetry = StartProofRecovery(Current, resume, resumePlaying);
                if (proofRetry is not null) return proofRetry;
            }
            if (!usingWebm && !recoveryAttempted && (args.Error == MediaPlayerError.NetworkError || rejectedUrl))
            {
                recoveryAttempted = true;
                return LoadAsync(Current, true, resume, resumePlaying);
            }
            // A CDN refusal affects both formats; changing codec must not hide that cause.
            if (!rejectedUrl && !usingWebm && !webmAttempted && (args.Error is MediaPlayerError.DecodingError or MediaPlayerError.SourceNotSupported))
            {
                webmAttempted = true;
                return LoadAsync(Current, false, resume, resumePlaying, webm: true);
            }
            DetachSource();
            IsBusy = false;
            SetStatus(rejectedUrl ? "AudioLinkRejected" : args.Error switch
            {
                MediaPlayerError.NetworkError => "AudioConnectionFailed",
                MediaPlayerError.DecodingError => "AudioDecodingFailed",
                MediaPlayerError.SourceNotSupported => "AudioSourceUnsupported",
                _ => "PlaybackFailedSelectAgain"
            }, error: true);
            return Task.CompletedTask;
        })));
    }

    private void PlaybackStateChanged(MediaPlaybackSession sender, object args) => Post(() =>
    {
        IsPlaying = HasSource && sender.PlaybackState == MediaPlaybackState.Playing;
        systemControls.PlaybackStatus = sender.PlaybackState switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped
        };
        if (!IsBusy && HasSource)
            SetStatus(IsPlaying ? "Playing" : sender.PlaybackState == MediaPlaybackState.Buffering ? "Buffering" : "Paused");
    });

    private void TimerTick(DispatcherQueueTimer sender, object args)
    {
        if (disposed || !HasSource || IsBusy) return;
        try
        {
            PositionSeconds = Math.Max(0, player.PlaybackSession.Position.TotalSeconds);
            DurationSeconds = Math.Max(0, player.PlaybackSession.NaturalDuration.TotalSeconds);
            UpdateTimeline();
        }
        catch (Exception) { SetStatus("PositionReadFailed", error: true); }
    }

    private void UpdateMetadata(MusicItem item)
    {
        var updater = systemControls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = item.Title;
        updater.MusicProperties.Artist = item.Subtitle;
        updater.Update();
    }

    private void UpdateTimeline()
    {
        systemControls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(DurationSeconds),
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromSeconds(DurationSeconds),
            Position = TimeSpan.FromSeconds(Math.Min(PositionSeconds, DurationSeconds))
        });
    }

    private void SystemSeekRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        => Seek(args.RequestedPlaybackPosition.TotalSeconds);

    private void SystemButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        => Post(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    playWhenOpened = true;
                    if (!IsBusy && HasSource) player.Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    playWhenOpened = false;
                    player.Pause();
                    break;
                case SystemMediaTransportControlsButton.Next: Observe(NextAsync()); break;
                case SystemMediaTransportControlsButton.Previous: Observe(PreviousAsync()); break;
                case SystemMediaTransportControlsButton.Stop: Observe(StopAsync()); break;
            }
        });

    private void DetachSource()
    {
        player.Pause();
        player.Source = null;
        source?.Dispose();
        directSource?.Dispose();
        source = null;
        directSource = null;
        IsPlaying = false;
    }

    private static bool IsPlayable(MusicItem item) => item.Available != false && !string.IsNullOrWhiteSpace(item.VideoId);
    private void SetStatus(string resourceKey, bool error = false)
    {
        HasError = error;
        StatusText = localize(resourceKey);
    }

    private string Describe(Exception error) => error switch
    {
        MusicCoreException { Code: "authentication_required" or "authentication_rejected" } => localize("PlaybackSessionExpired"),
        MusicCoreException { Code: "rate_limited" } => localize("PlaybackRateLimited"),
        MusicCoreException { Code: "po_token_required" } => localize("OfficialVerificationRequired"),
        MusicCoreException { Code: "attestation_failed" or "runtime_missing" } => localize("PlaybackVerificationFailed"),
        MusicCoreException { Code: "timeout" } => localize("AudioRequestTimeout"),
        MusicCoreException { Code: "sabr_required" or "sabr_reload_required" } => localize("SabrNotSupported"),
        MusicCoreException { Code: "unplayable" } => localize("PlaybackSongUnavailable"),
        MusicCoreException { Code: "stream_unavailable" } => localize("NoAacSource"),
        NotSupportedException => localize("DashUnsupported"),
        _ => localize("AudioLoadFailed")
    };

    private Task Track(Task task)
    {
        lock (tasksLock) pendingLoads.Add(task);
        _ = task.ContinueWith(completed => { lock (tasksLock) pendingLoads.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private void Observe(Task task)
    {
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception) { Post(() => SetStatus("PlaybackOperationFailed", error: true)); }
    }

    private void Post(Action action)
    {
        void SafeAction()
        {
            if (disposed || stopping || closing) return;
            try { action(); }
            catch (Exception) { SetStatus("PlaybackOperationFailed", error: true); }
        }
        if (dispatcher.HasThreadAccess) SafeAction();
        else dispatcher.TryEnqueue(SafeAction);
    }

    private Task OnUiAsync(Func<Task> action)
    {
        if (disposed) return Task.CompletedTask;
        if (dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { if (!disposed) await action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new InvalidOperationException("Window dispatcher has stopped."));
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await OnUiAsync(() => { closing = true; return Task.CompletedTask; });
        await StopAsync();
        await OnUiAsync(() =>
        {
            disposed = true;
            timer.Stop();
            timer.Tick -= TimerTick;
            systemControls.ButtonPressed -= SystemButtonPressed;
            systemControls.PlaybackPositionChangeRequested -= SystemSeekRequested;
            systemControls.IsEnabled = false;
            player.MediaOpened -= MediaOpened;
            player.MediaEnded -= MediaEnded;
            player.MediaFailed -= MediaFailed;
            player.PlaybackSession.PlaybackStateChanged -= PlaybackStateChanged;
            player.Dispose();
            return Task.CompletedTask;
        });
    }
}
