using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Music.Desktop.Models;
using Windows.Media;
using Windows.Media.Playback;
using YouTubeMusic.Interop;

namespace Music.Desktop.Services;

/// <summary>Native AAC/DASH playback. Signed descriptors exist only for the active source.</summary>
public sealed class PlaybackService : ObservableObject, IAsyncDisposable
{
    private readonly CoreService core;
    private readonly DispatcherQueue dispatcher;
    private readonly MediaPlayer player = new() { AutoPlay = false };
    private readonly DispatcherQueueTimer timer;
    private readonly SystemMediaTransportControls systemControls;
    private readonly object tasksLock = new();
    private readonly HashSet<Task> pendingLoads = [];
    private WindowsMusicSource? source;
    private CancellationTokenSource? loadCancellation;
    private long generation;
    private bool disposed;
    private bool stopping;
    private bool closing;
    private bool recoveryAttempted;
    private double pendingSeek;
    private bool playWhenOpened;
    private MusicItem? current;
    private bool isPlaying;
    private bool isBusy;
    private bool shuffle;
    private int repeatMode;
    private double positionSeconds;
    private double durationSeconds;
    private double volume = .75;
    private string statusText = "选择一首歌曲开始播放";

    public PlaybackService(CoreService core, DispatcherQueue dispatcher)
    {
        this.core = core;
        this.dispatcher = dispatcher;
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
                StatusText = "这个条目无法直接播放，请打开歌单或专辑";
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
        if (source is null) return;
        playWhenOpened = !IsPlaying;
        if (IsPlaying) player.Pause(); else player.Play();
    });

    public Task NextAsync() => Track(OnUiAsync(() => MoveAsync(1, false)));
    public Task PreviousAsync() => Track(OnUiAsync(() =>
    {
        if (PositionSeconds > 3 && source is not null) { Seek(0); return Task.CompletedTask; }
        return MoveAsync(-1, false);
    }));

    public void Enqueue(MusicItem item) => Post(() =>
    {
        if (IsPlayable(item)) { Queue.Add(item); StatusText = "已加入播放队列"; }
    });

    public void Seek(double seconds) => Post(() =>
    {
        if (!double.IsFinite(seconds)) return;
        double position = Math.Clamp(seconds, 0, DurationSeconds > 0 ? DurationSeconds : double.MaxValue);
        if (IsBusy) { pendingSeek = position; return; }
        if (source is null || !player.PlaybackSession.CanSeek) return;
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
            StatusText = "已停止播放";
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

    private async Task LoadAsync(MusicItem item, bool refresh, double seek, bool autoPlay)
    {
        long requestGeneration = ++generation;
        loadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        DetachSource();
        Current = item;
        PositionSeconds = seek;
        DurationSeconds = 0;
        IsBusy = true;
        pendingSeek = seek;
        playWhenOpened = autoPlay;
        if (!refresh) recoveryAttempted = false;
        StatusText = refresh ? "正在重新获取播放源…" : "正在准备音频…";
        WindowsMusicSource? replacement = null;
        try
        {
            if (refresh)
                await core.CallAsync(new { op = "stream_refresh", video_id = item.VideoId, format = "mp4" }, cancellation.Token);
            var manifest = await core.CallAsync(new { op = "dash_manifest", video_id = item.VideoId }, cancellation.Token);
            replacement = await WindowsMusicSource.FromManifestAsync(manifest, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // Discard any success racing with cancellation, account change or a newer selection.
            if (disposed || requestGeneration != generation) return;
            source = replacement;
            replacement = null;
            player.Source = source.Source;
            UpdateMetadata(item);
            StatusText = "正在加载音频…";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!disposed && requestGeneration == generation)
            {
                DetachSource();
                StatusText = Describe(error);
            }
        }
        finally
        {
            replacement?.Dispose();
            if (requestGeneration == generation)
            {
                loadCancellation = null;
                // MediaOpened clears the busy state after native initialization succeeds.
                if (source is null) IsBusy = false;
            }
        }
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
                if (automatic) { player.Pause(); IsPlaying = false; StatusText = "队列已播放完毕"; }
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
            if (observed != generation || source is null) return;
            IsBusy = false;
            DurationSeconds = Math.Max(0, player.PlaybackSession.NaturalDuration.TotalSeconds);
            if (pendingSeek > 0 && player.PlaybackSession.CanSeek)
                player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Min(pendingSeek, Math.Max(0, DurationSeconds - .1)));
            pendingSeek = 0;
            if (playWhenOpened) player.Play();
            StatusText = playWhenOpened ? "正在播放" : "已暂停";
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
            if (stopping || closing || observed != generation || source is null || Current is null) return Task.CompletedTask;
            if (!recoveryAttempted && args.Error == MediaPlayerError.NetworkError)
            {
                recoveryAttempted = true;
                double resume = Math.Max(PositionSeconds, player.PlaybackSession.Position.TotalSeconds);
                return LoadAsync(Current, true, resume, IsPlaying || playWhenOpened);
            }
            DetachSource();
            IsBusy = false;
            StatusText = args.Error switch
            {
                MediaPlayerError.NetworkError => "音频连接失败，请检查网络后重试",
                MediaPlayerError.DecodingError => "Windows 无法解码这首歌曲的音频",
                MediaPlayerError.SourceNotSupported => "Windows 不支持这个音频源",
                _ => "播放失败，请重新选择歌曲重试"
            };
            return Task.CompletedTask;
        })));
    }

    private void PlaybackStateChanged(MediaPlaybackSession sender, object args) => Post(() =>
    {
        IsPlaying = source is not null && sender.PlaybackState == MediaPlaybackState.Playing;
        systemControls.PlaybackStatus = sender.PlaybackState switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped
        };
        if (!IsBusy && source is not null)
            StatusText = IsPlaying ? "正在播放" : sender.PlaybackState == MediaPlaybackState.Buffering ? "正在缓冲…" : "已暂停";
    });

    private void TimerTick(DispatcherQueueTimer sender, object args)
    {
        if (disposed || source is null || IsBusy) return;
        try
        {
            PositionSeconds = Math.Max(0, player.PlaybackSession.Position.TotalSeconds);
            DurationSeconds = Math.Max(0, player.PlaybackSession.NaturalDuration.TotalSeconds);
            UpdateTimeline();
        }
        catch (Exception) { StatusText = "暂时无法读取播放进度"; }
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
                    if (IsBusy) playWhenOpened = true; else if (source is not null) player.Play();
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
        source = null;
        IsPlaying = false;
    }

    private static bool IsPlayable(MusicItem item) => item.Available != false && !string.IsNullOrWhiteSpace(item.VideoId);
    private static string Describe(Exception error) => error switch
    {
        MusicCoreException { Code: "authentication_required" or "authentication_rejected" } => "登录已失效，请重新导入 Cookie",
        MusicCoreException { Code: "rate_limited" } => "请求过于频繁，请稍后再试",
        MusicCoreException { Code: "po_token_required" } => "这首歌曲需要额外的官方播放验证，暂时无法播放",
        MusicCoreException { Code: "timeout" } => "获取音频超时，请重试",
        MusicCoreException { Code: "sabr_required" or "sabr_reload_required" } => "这首歌曲需要 SABR 音频，目前客户端尚未接入",
        MusicCoreException { Code: "unplayable" } => "这首歌曲当前无法播放",
        MusicCoreException { Code: "stream_unavailable" } => "这首歌曲暂时没有可用的 AAC 音频源",
        NotSupportedException => "当前 Windows 系统不支持 DASH 音频播放",
        _ => "无法加载音频，请检查登录状态或网络后重试"
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
        catch (Exception) { Post(() => StatusText = "播放操作失败，请重试"); }
    }

    private void Post(Action action)
    {
        void SafeAction()
        {
            if (disposed || stopping || closing) return;
            try { action(); }
            catch (Exception) { StatusText = "播放操作失败，请重试"; }
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
