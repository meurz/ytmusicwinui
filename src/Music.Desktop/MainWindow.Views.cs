using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Music.Desktop.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Music.Desktop;
public sealed partial class MainWindow
{
    private UIElement FeatureCard(MusicItem item)
    {
        var grid = new Grid { MinHeight = 220, Background = Accent, CornerRadius = new CornerRadius(13), ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        var copy = new StackPanel { Spacing = 14, Margin = new Thickness(30), VerticalAlignment = VerticalAlignment.Center };
        var eyebrow = Text("为此刻的你", 11, White); eyebrow.CharacterSpacing = 130; copy.Children.Add(eyebrow);
        var title = Text(item.Title, 28, White); title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis; copy.Children.Add(title);
        copy.Children.Add(Text(string.IsNullOrWhiteSpace(item.Subtitle) ? "音乐响起，给自己一段留白。" : item.Subtitle, 12, White));
        var open = ActionButton(item.VideoId is null ? "打开歌单  ›" : "▶  立即播放", () => OpenItemAsync(item, currentPage?.Items), false); open.Background = White; open.Foreground = Accent; copy.Children.Add(open);
        grid.Children.Add(copy);
        var cover = new Image { Stretch = Stretch.UniformToFill, Height = 220, Width = 240 }; SetImage(cover, item.ImageUrl);
        var frame = new Border { CornerRadius = new CornerRadius(0, 13, 13, 0), Child = cover }; Grid.SetColumn(frame, 1); grid.Children.Add(frame);
        return grid;
    }

    private GridView CardList(IReadOnlyList<MusicItem> items)
    {
        var list = new GridView { ItemsSource = items, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(-6, 0, -6, 0) };
        list.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
          <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsWrapGrid Orientation="Horizontal" /></ItemsPanelTemplate>
          """);
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
          <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Width="170" Spacing="8" Margin="5,4,5,12">
              <Border Width="170" Height="170" Background="#E3EADF" CornerRadius="11">
                <Grid><FontIcon Glyph="&#xE8D6;" FontSize="38" Foreground="#7A877E"/><Image Source="{Binding ImageUrl}" Stretch="UniformToFill"/></Grid>
              </Border>
              <TextBlock Text="{Binding Title}" FontSize="13" FontWeight="SemiBold" Foreground="#263B31" MaxLines="2" TextWrapping="Wrap" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Subtitle}" FontSize="11" Foreground="#7A877E" MaxLines="1" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
          </DataTemplate>
          """);
        list.ItemClick += async (_, args) => await GuardAsync(() => OpenItemAsync((MusicItem)args.ClickedItem, currentPage?.Items ?? items));
        list.RightTapped += (_, args) => ShowItemMenu(list, args.OriginalSource as DependencyObject, args.GetPosition(list));
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }
    private ListView TrackList(IEnumerable<MusicItem> source)
    {
        var items = source.ToArray();
        var list = new ListView { ItemsSource = items, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, HorizontalAlignment = HorizontalAlignment.Stretch };
        list.ItemContainerStyle = (Style)XamlReader.Load("""
          <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem"><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Padding" Value="8,7"/><Setter Property="MinHeight" Value="60"/><Setter Property="CornerRadius" Value="8"/></Style>
          """);
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
          <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid ColumnSpacing="13">
              <Grid.ColumnDefinitions><ColumnDefinition Width="42"/><ColumnDefinition Width="*"/><ColumnDefinition Width="64"/></Grid.ColumnDefinitions>
              <Border Height="42" Width="42" CornerRadius="6" Background="#E3EADF"><Grid><FontIcon Glyph="&#xE8D6;" FontSize="17" Foreground="#7A877E"/><Image Source="{Binding ImageUrl}" Stretch="UniformToFill"/></Grid></Border>
              <StackPanel Grid.Column="1" Spacing="5" VerticalAlignment="Center"><TextBlock Text="{Binding Title}" FontSize="13" Foreground="#263B31" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding Subtitle}" FontSize="11" Foreground="#7A877E" TextTrimming="CharacterEllipsis"/></StackPanel>
              <TextBlock Grid.Column="2" Text="{Binding DurationText}" Foreground="#7A877E" FontSize="11" HorizontalAlignment="Right" VerticalAlignment="Center"/>
            </Grid>
          </DataTemplate>
          """);
        list.ItemClick += async (_, args) => await GuardAsync(() => OpenItemAsync((MusicItem)args.ClickedItem, currentPage?.Items ?? items));
        list.RightTapped += (_, args) => ShowItemMenu(list, args.OriginalSource as DependencyObject, args.GetPosition(list));
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }
    private void ShowItemMenu(FrameworkElement target, DependencyObject? source, Windows.Foundation.Point position)
    {
        MusicItem? item = null;
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element && element.DataContext is MusicItem music) { item = music; break; }
            if (node == target) break;
        }
        if (item is null) return;
        var selected = item; var menu = new MenuFlyout();
        if (selected.VideoId is not null)
        {
            AddMenu(menu, "播放", () => OpenItemAsync(selected, currentPage?.Items));
            AddMenu(menu, "加入播放队列", () => { playback?.Enqueue(selected); ShowNotice("已加入播放队列"); return Task.CompletedTask; });
            AddMenu(menu, selected.Actions.Rating == "like" ? "取消喜欢" : "喜欢", () => RateAsync(selected, selected.Actions.Rating == "like" ? "indifferent" : "like"));
            AddMenu(menu, "添加到歌单…", () => ShowAddToPlaylistAsync(selected));
            AddMenu(menu, "复制歌曲链接", () => { var data = new DataPackage(); data.SetText("https://music.youtube.com/watch?v=" + Uri.EscapeDataString(selected.VideoId)); Clipboard.SetContent(data); ShowNotice("歌曲链接已复制"); return Task.CompletedTask; });
            if (currentPage?.Actions.CanEdit == true && currentPage.PlaylistId is not null && selected.SetVideoId is not null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                AddMenu(menu, "从歌单移除", () => RemovePlaylistEntryAsync(selected));
                AddMenu(menu, "移到歌单末尾", async () => await MutateAndReloadAsync(new { op = "move_playlist_item", playlist_id = currentPage.PlaylistId, set_video_id = selected.SetVideoId }));
            }
        }
        else AddMenu(menu, "打开", () => OpenItemAsync(selected));
        if (selected.Actions.AddLibraryToken is not null) AddMenu(menu, "保存到音乐库", () => MutateAndReloadAsync(new { op = "edit_library", feedback_tokens = new[] { selected.Actions.AddLibraryToken } }));
        if (selected.Actions.RemoveLibraryToken is not null) AddMenu(menu, "从音乐库移除", () => MutateAndReloadAsync(new { op = "edit_library", feedback_tokens = new[] { selected.Actions.RemoveLibraryToken } }));
        if (selected.BrowseId?.StartsWith("UC", StringComparison.Ordinal) == true)
            AddMenu(menu, selected.Actions.Subscribed == true ? "取消订阅" : "订阅艺人", () => MutateAndReloadAsync(new { op = "subscribe", channel_id = selected.BrowseId, subscribed = selected.Actions.Subscribed != true }));
        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }
    private void AddMenu(MenuFlyout menu, string text, Func<Task> action) { var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await GuardAsync(action); menu.Items.Add(item); }
    private void AddPageActions()
    {
        if (currentPage is null) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var playable = currentPage.Items.Where(i => i.VideoId is not null && i.Available != false).ToArray();
        if (playable.Length > 0)
        {
            row.Children.Add(ActionButton("▶  播放全部", () => OpenItemAsync(currentPage!.Items.First(i => i.VideoId is not null && i.Available != false), currentPage.Items), true));
            row.Children.Add(ActionButton("＋ 加入队列", () => { foreach (var item in currentPage!.Items.Where(i => i.VideoId is not null && i.Available != false)) playback?.Enqueue(item); ShowNotice("已加入播放队列"); return Task.CompletedTask; }));
        }
        if (currentPage.Actions.CanEdit == true && currentPage.PlaylistId is not null)
        {
            row.Children.Add(ActionButton("编辑歌单", ShowEditPlaylistAsync)); row.Children.Add(ActionButton("删除歌单", ShowDeletePlaylistAsync));
        }
        else if (currentPage.PlaylistId is not null)
            row.Children.Add(ActionButton(currentPage.Actions.Rating == "like" ? "取消收藏" : "收藏歌单", () => MutateAndReloadAsync(new { op = "rate_playlist", playlist_id = currentPage.PlaylistId, rating = currentPage.Actions.Rating == "like" ? "indifferent" : "like" })));
        if (row.Children.Count > 0) pageBody.Children.Add(HorizontalScroll(row));
    }
    private async Task RateAsync(MusicItem item, string rating)
    {
        if (!await RequireAccountAsync() || core is null) return;
        await core.CallAsync(new { op = "rate_song", video_id = item.VideoId, rating }, lifetime.Token);
        ShowNotice(rating == "like" ? "已喜欢这首歌曲" : "已取消喜欢");
        if (item.VideoId == playback?.Current?.VideoId) { likeButton.Content = Icon(rating == "like" ? "\uEB52" : "\uEB51", 18, Accent); playerRating = rating; }
        // Actions contain state-specific tokens: refresh rather than replaying stale values.
        if (currentView != "settings" && currentView != "recent") await NavigateAsync(currentView, currentRequest, currentTitle, false);
    }
    private async Task MutateAndReloadAsync(object request)
    {
        if (!await RequireAccountAsync() || core is null) return;
        await core.CallAsync(request, lifetime.Token);
        await NavigateAsync(currentView, currentRequest, currentTitle, false); ShowNotice("修改已提交，页面已重新读取");
    }
}
