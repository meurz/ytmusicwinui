using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Music.Desktop.Models;
using Music.Desktop.Services;

namespace Music.Desktop;
public sealed partial class MainWindow
{
    private string? playerRating;
    private Border CreatePlayer()
    {
        var grid = new Grid { ColumnSpacing = 22, Padding = new Thickness(22, 12, 22, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        var metadata = new Grid { ColumnSpacing = 11 };
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) }); metadata.ColumnDefinitions.Add(new ColumnDefinition()); metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metadata.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Brush(225, 232, 220), Width = 52, Height = 52, Child = playerCover });
        var trackInfo = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        trackTitle.MaxLines = 1; trackTitle.TextTrimming = TextTrimming.CharacterEllipsis; trackArtist.MaxLines = 1; trackArtist.TextTrimming = TextTrimming.CharacterEllipsis;
        trackInfo.Children.Add(trackTitle); trackInfo.Children.Add(trackArtist); Grid.SetColumn(trackInfo, 1); metadata.Children.Add(trackInfo);
        Grid.SetColumn(likeButton, 2); likeButton.IsEnabled = false; likeButton.Click += async (_, _) => { if (playback?.Current is { } item) await GuardAsync(() => RateAsync(item, playerRating == "like" ? "indifferent" : "like")); }; metadata.Children.Add(likeButton);
        grid.Children.Add(metadata);
        var transport = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        shuffleButton.Click += (_, _) => { if (playback is not null) playback.Shuffle = shuffleButton.IsChecked == true; };
        var previous = IconButton("\uE892", "上一首"); previous.Click += async (_, _) => { if (playback is not null) await GuardAsync(playback.PreviousAsync); };
        playButton.Background = Accent; playButton.CornerRadius = new CornerRadius(18); playButton.Content = Icon("\uE768", 17, White); playButton.Click += (_, _) => playback?.TogglePlayPause(); AutomationProperties.SetAutomationId(playButton, "PlayPauseButton");
        var next = IconButton("\uE893", "下一首"); next.Click += async (_, _) => { if (playback is not null) await GuardAsync(playback.NextAsync); };
        repeatButton.Click += (_, _) => { if (playback is null) return; playback.RepeatMode = (playback.RepeatMode + 1) % 3; repeatButton.IsChecked = playback.RepeatMode != 0; ToolTipService.SetToolTip(repeatButton, new[] { "循环关闭", "列表循环", "单曲循环" }[playback.RepeatMode]); };
        controls.Children.Add(shuffleButton); controls.Children.Add(previous); controls.Children.Add(playButton); controls.Children.Add(next); controls.Children.Add(repeatButton); transport.Children.Add(controls);
        var timeline = new Grid { ColumnSpacing = 10 }; timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37) }); timeline.ColumnDefinitions.Add(new ColumnDefinition()); timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37) });
        timeline.Children.Add(positionLabel); Grid.SetColumn(seekSlider, 1); timeline.Children.Add(seekSlider); Grid.SetColumn(durationLabel, 2); timeline.Children.Add(durationLabel);
        AutomationProperties.SetName(seekSlider, "播放进度"); AutomationProperties.SetAutomationId(seekSlider, "SeekSlider");
        seekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => draggingSeek = true), true);
        seekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { draggingSeek = false; playback?.Seek(seekSlider.Value); }), true);
        seekSlider.PointerCaptureLost += (_, _) => { if (draggingSeek) { draggingSeek = false; playback?.Seek(seekSlider.Value); } };
        seekSlider.ValueChanged += (_, args) => { if (!updatingSeek && !draggingSeek) playback?.Seek(args.NewValue); };
        transport.Children.Add(timeline); Grid.SetColumn(transport, 1); grid.Children.Add(transport);
        var extras = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right };
        lyricsButton.Click += async (_, _) => { if (lyricsButton.IsChecked == true) await ShowLyricsAsync(); else ClosePanel(); };
        queueButton.Click += (_, _) => { if (queueButton.IsChecked == true) { lyricsLoad?.Cancel(); panelMode = "queue"; sidePanel.Visibility = Visibility.Visible; lyricsButton.IsChecked = false; RenderQueue(); } else ClosePanel(); };
        var volume = IconButton("\uE767", "静音或恢复音量"); double oldVolume = .7;
        volume.Click += (_, _) => { if (playback is null) return; if (playback.Volume > 0) { oldVolume = playback.Volume; playback.Volume = 0; } else playback.Volume = oldVolume; settingVolume = true; volumeSlider.Value = playback.Volume * 100; settingVolume = false; };
        volumeSlider.ValueChanged += (_, args) => { if (!settingVolume && playback is not null) playback.Volume = args.NewValue / 100; };
        AutomationProperties.SetName(volumeSlider, "音量"); row.Children.Add(lyricsButton); row.Children.Add(queueButton); row.Children.Add(volume); row.Children.Add(volumeSlider); extras.Children.Add(row);
        playStatus.HorizontalAlignment = HorizontalAlignment.Right; playStatus.MaxLines = 1; playStatus.TextTrimming = TextTrimming.CharacterEllipsis; extras.Children.Add(playStatus); Grid.SetColumn(extras, 2); grid.Children.Add(extras);
        return new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
    }
    private void PlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (playback is null || closing) return;
        var current = playback.Current;
        if (current is not null)
        {
            trackTitle.Text = current.Title; trackArtist.Text = current.Subtitle; likeButton.IsEnabled = true;
            if (current.VideoId != observedVideo)
            {
                observedVideo = current.VideoId; playerRating = current.Actions.Rating; SetImage(playerCover, current.ImageUrl);
                likeButton.Content = Icon(playerRating == "like" ? "\uEB52" : "\uEB51", 18, Accent);
                if (sidePanel.Visibility == Visibility.Visible && panelMode == "lyrics") _ = GuardAsync(ShowLyricsAsync);
            }
            if (playback.IsPlaying && !recent.Any(i => i.VideoId == current.VideoId)) { recent.Insert(0, current); if (recent.Count > 100) recent.RemoveAt(recent.Count - 1); }
        }
        playButton.Content = Icon(playback.IsPlaying ? "\uE769" : "\uE768", 17, White); playButton.IsEnabled = current is not null && !playback.IsBusy;
        playStatus.Text = playback.StatusText; ToolTipService.SetToolTip(playStatus, playback.StatusText);
        if (args.PropertyName == nameof(PlaybackService.StatusText) && !playback.IsBusy &&
            (playback.StatusText.Contains("失败") || playback.StatusText.Contains("拒绝") || playback.StatusText.Contains("无法")))
            ShowNotice(playback.StatusText, InfoBarSeverity.Warning);
        positionLabel.Text = FormatTime(playback.PositionSeconds); durationLabel.Text = FormatTime(playback.DurationSeconds);
        if (!draggingSeek) { updatingSeek = true; seekSlider.Maximum = Math.Max(1, playback.DurationSeconds); seekSlider.Value = Math.Clamp(playback.PositionSeconds, 0, seekSlider.Maximum); updatingSeek = false; }
        if (args.PropertyName == nameof(PlaybackService.Current) && sidePanel.Visibility == Visibility.Visible && panelMode == "queue") RenderQueue();
    }
    private void ClosePanel() { sidePanel.Visibility = Visibility.Collapsed; queueButton.IsChecked = false; lyricsButton.IsChecked = false; lyricsLoad?.Cancel(); }
    private void PanelHeader(string title)
    {
        panelBody.Children.Clear(); var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.Children.Add(Text(title, 20)); var close = IconButton("\uE711", "关闭侧栏"); close.Click += (_, _) => ClosePanel(); Grid.SetColumn(close, 1); row.Children.Add(close); panelBody.Children.Add(row);
    }
    private void RenderQueue()
    {
        if (closing || sidePanel.Visibility != Visibility.Visible || panelMode != "queue") return;
        PanelHeader("播放队列"); panelBody.Children.Add(Text($"{playback?.Queue.Count ?? 0} 首歌曲", 12, Muted));
        if (playback is null || playback.Queue.Count == 0) { panelBody.Children.Add(Text("播放一首歌，或右键加入队列。", 13, Muted)); return; }
        foreach (var item in playback.Queue.ToArray())
        {
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = ReferenceEquals(item, playback.Current) ? Brush(228, 237, 221) : Card, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
            var row = new Grid { ColumnSpacing = 10 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var image = new Image { Width = 38, Height = 38, Stretch = Stretch.UniformToFill }; SetImage(image, item.ImageUrl); row.Children.Add(new Border { Child = image, CornerRadius = new CornerRadius(5) });
            var meta = new StackPanel { Spacing = 5 }; var title = Text(item.Title, 12); title.MaxLines = 1; title.TextTrimming = TextTrimming.CharacterEllipsis; meta.Children.Add(title); var sub = Text(item.Subtitle, 10, Muted); sub.MaxLines = 1; sub.TextTrimming = TextTrimming.CharacterEllipsis; meta.Children.Add(sub); Grid.SetColumn(meta, 1); row.Children.Add(meta); button.Content = row;
            button.Click += async (_, _) => await GuardAsync(() => playback.PlayAsync(item)); panelBody.Children.Add(button);
        }
    }
    private async Task ShowLyricsAsync()
    {
        panelMode = "lyrics"; sidePanel.Visibility = Visibility.Visible; lyricsButton.IsChecked = true; queueButton.IsChecked = false;
        var item = playback?.Current; PanelHeader("歌词");
        if (item?.VideoId is null || core is null) { panelBody.Children.Add(Text("选择一首歌曲查看歌词", 14, Muted)); return; }
        var cover = new Image { Width = 240, Height = 240, Stretch = Stretch.UniformToFill }; SetImage(cover, item.ImageUrl); panelBody.Children.Add(new Border { CornerRadius = new CornerRadius(12), Child = cover });
        panelBody.Children.Add(Text(item.Title, 20)); panelBody.Children.Add(Text(item.Subtitle, 12, Muted));
        if (loadedLyricsVideo == item.VideoId) { panelBody.Children.Add(lyricText); return; }
        lyricsLoad?.Cancel(); lyricsLoad?.Dispose(); lyricsLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = lyricsLoad.Token;
        lyricText.Text = "正在加载歌词…"; lyricText.LineHeight = 35; panelBody.Children.Add(lyricText);
        try
        {
            var result = await core.CallAsync(new { op = "timed_lyrics", video_id = item.VideoId }, token); token.ThrowIfCancellationRequested();
            if (panelMode != "lyrics" || playback?.Current?.VideoId != item.VideoId) return;
            loadedLyricsVideo = item.VideoId; lyricText.Text = JsonString(result, "text") ?? "暂无歌词";
            string? source = JsonString(result, "source"); if (!string.IsNullOrEmpty(source)) panelBody.Children.Add(Text(source, 10, Muted));
        }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested) lyricText.Text = "这首歌曲暂时没有可用歌词。"; }
    }
    private static string FormatTime(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}"; }
}
