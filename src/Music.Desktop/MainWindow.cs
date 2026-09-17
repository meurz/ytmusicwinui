using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Music.Desktop.Models;
using Music.Desktop.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinUIEx;
using YouTubeMusic.Interop;

namespace Music.Desktop;

public sealed partial class MainWindow : WindowEx
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? pageLoad, suggestionLoad, lyricsLoad;
    private CoreService? core;
    private PlaybackService? playback;
    private readonly StackPanel pageBody = new() { Spacing = 25, Margin = new Thickness(32, 10, 32, 35) };
    private readonly ScrollViewer pageScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly NavigationView navigation = new();
    private readonly AutoSuggestBox search = new() { PlaceholderText = "搜索歌曲、艺人或歌单", QueryIcon = new SymbolIcon(Symbol.Find), MaxWidth = 430, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(24, 0, 24, 12) };
    private readonly ProgressBar loading = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Height = 3 };
    private readonly Button accountButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button backButton = IconButton("\uE72B", "后退");
    private readonly StackPanel panelBody = new() { Spacing = 18, Margin = new Thickness(22) };
    private readonly Border sidePanel = new() { Width = 305, Visibility = Visibility.Collapsed, Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly Stack<(string View, Dictionary<string, object?> Request, string Title)> history = new();
    private Dictionary<string, object?> currentRequest = new() { ["op"] = "home" };
    private string currentView = "home", currentTitle = "好音乐，慢慢听。", selectedLibrary = "playlists", searchFilter = "all", panelMode = "queue";
    private MusicPage? currentPage;
    private readonly List<MusicItem> recent = new();
    private string? loadedLyricsVideo;
    private readonly TextBlock lyricText = Text("选择一首歌曲查看歌词", 17, Muted);
    private readonly TextBlock trackTitle = Text("选择一首歌，开始播放", 13);
    private readonly TextBlock trackArtist = Text("ytmusicwinui", 11, Muted);
    private readonly TextBlock playStatus = Text("", 10, Muted);
    private readonly TextBlock positionLabel = Text("0:00", 10, Muted), durationLabel = Text("0:00", 10, Muted);
    private readonly Image playerCover = new() { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
    private readonly Button playButton = IconButton("\uE768", "播放或暂停"), likeButton = IconButton("\uEB51", "喜欢当前歌曲");
    private readonly ToggleButton shuffleButton = ToggleIcon("\uE8B1", "随机播放"), repeatButton = ToggleIcon("\uE8EE", "循环模式"), queueButton = ToggleIcon("\uE8FD", "播放队列"), lyricsButton = ToggleIcon("\uE8A5", "歌词");
    private readonly Slider seekSlider = new() { Minimum = 0, Maximum = 1, Value = 0, MinWidth = 140, Height = 24 };
    private readonly Slider volumeSlider = new() { Minimum = 0, Maximum = 100, Value = 70, Width = 85, Height = 28 };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer maintenanceTimer;
    private bool updatingSeek, draggingSeek, closing, cleanupComplete, dialogOpen, settingVolume, refreshingSession;
    private string? observedVideo;

    public MainWindow()
    {
        Title = "ytmusicwinui"; Width = 1280; Height = 850; MinWidth = 900; MinHeight = 660;
        PersistenceId = "ytmusicwinui.MainWindow";
        ExtendsContentIntoTitleBar = true;
        var layout = new Grid { Background = BackgroundBrush };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        var titlebar = new Grid { Background = BackgroundBrush, Padding = new Thickness(22, 0, 145, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border { Background = Accent, CornerRadius = new CornerRadius(6), Width = 24, Height = 24, Child = Icon("\uE8D6", 14, White) });
        brand.Children.Add(Text("ytmusicwinui", 12));
        titlebar.Children.Add(brand); layout.Children.Add(titlebar); SetTitleBar(titlebar);
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 38, 59, 49);
        ConfigureNavigation(); Grid.SetRow(navigation, 1); layout.Children.Add(navigation);
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var workspace = new Grid(); workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new Grid { Margin = new Thickness(25, 16, 25, 16), ColumnSpacing = 15 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backButton.IsEnabled = false; backButton.Click += (_, _) => GoBack(); toolbar.Children.Add(backButton);
        Grid.SetColumn(search, 1); toolbar.Children.Add(search); AutomationProperties.SetAutomationId(search, "SearchInput");
        var refresh = IconButton("\uE72C", "刷新页面"); refresh.Click += async (_, _) => { if (currentView == "settings") ShowSettings(false); else if (currentView == "recent") ShowRecent(false); else await NavigateAsync(currentView, currentRequest, currentTitle, false); }; Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        workspace.Children.Add(toolbar); Grid.SetRow(notice, 1); workspace.Children.Add(notice);
        var bodyLayer = new Grid(); pageScroll.Content = pageBody; bodyLayer.Children.Add(pageScroll); loading.VerticalAlignment = VerticalAlignment.Top; bodyLayer.Children.Add(loading); Grid.SetRow(bodyLayer, 2); workspace.Children.Add(bodyLayer);
        content.Children.Add(workspace); sidePanel.Child = new ScrollViewer { Content = panelBody, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(sidePanel, 1); content.Children.Add(sidePanel);
        navigation.Content = content;
        var player = CreatePlayer(); Grid.SetRow(player, 2); layout.Children.Add(player);
        Content = layout;
        search.QuerySubmitted += async (_, args) => { string query = args.ChosenSuggestion?.ToString() ?? args.QueryText; search.ItemsSource = null; if (!string.IsNullOrWhiteSpace(query)) await NavigateAsync("search", new() { ["op"] = "search", ["query"] = query.Trim(), ["filter"] = searchFilter }, query.Trim()); };
        search.TextChanged += SearchTextChanged;
        var searchShortcut = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        searchShortcut.Invoked += (_, args) => { search.Focus(FocusState.Keyboard); args.Handled = true; }; layout.KeyboardAccelerators.Add(searchShortcut);
        maintenanceTimer = DispatcherQueue.CreateTimer(); maintenanceTimer.Interval = TimeSpan.FromMinutes(10); maintenanceTimer.Tick += async (_, _) => await MaintainSessionAsync();
        Closed += (_, _) => { if (!closing) _ = DisposeServicesAsync(); };
        AppWindow.Closing += async (_, args) => { if (cleanupComplete) return; args.Cancel = true; if (closing) return; await DisposeServicesAsync(); cleanupComplete = true; Close(); };
        pageBody.Children.Add(Heading("欢迎回来", "正在连接你的音乐…", "YOUR MUSIC, YOUR MOMENT"));
        _ = InitializeAsync();
    }

    private void ConfigureNavigation()
    {
        navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        navigation.OpenPaneLength = 210; navigation.CompactPaneLength = 48; navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        navigation.IsPaneToggleButtonVisible = false; navigation.IsSettingsVisible = true; navigation.AlwaysShowHeader = false;
        navigation.MenuItems.Add(NavItem("首页", "\uE80F", "home"));
        navigation.MenuItems.Add(NavItem("发现", "\uE707", "explore"));
        navigation.MenuItems.Add(NavItem("音乐库", "\uE8F1", "library"));
        navigation.MenuItems.Add(new NavigationViewItemSeparator());
        navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "我的收藏" });
        navigation.MenuItems.Add(NavItem("喜欢的音乐", "\uEB51", "likes"));
        navigation.MenuItems.Add(NavItem("最近播放", "\uE81C", "recent"));
        navigation.SelectedItem = navigation.MenuItems[0];
        navigation.ItemInvoked += async (_, args) =>
        {
            if (args.IsSettingsInvoked) { ShowSettings(); return; }
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string view)
            {
                switch (view)
                {
                    case "home": await NavigateAsync(view, new() { ["op"] = "home" }, "好音乐，慢慢听。"); break;
                    case "explore": await NavigateAsync(view, new() { ["op"] = "explore" }, "发现新的心动"); break;
                    case "library": await LoadLibraryAsync(selectedLibrary); break;
                    case "likes": await NavigateAsync("likes", new() { ["op"] = "library", ["section"] = "likes" }, "喜欢的音乐"); break;
                    case "recent": ShowRecent(); break;
                }
            }
        };
        accountButton.Content = Horizontal(Icon("\uE77B", 18), Text("导入账户", 12));
        accountButton.Click += async (_, _) => { if (core?.IsAuthenticated == true) await ShowAccountDialogAsync(); else await ShowImportDialogAsync(); };
        accountButton.Margin = new Thickness(10, 12, 10, 18); accountButton.Background = Card;
        navigation.PaneFooter = accountButton;
    }

    private async Task InitializeAsync()
    {
        loading.Visibility = Visibility.Visible;
        try
        {
            core = await CoreService.CreateAsync(lifetime.Token);
            if (closing) { await core.DisposeAsync(); return; }
            playback = new PlaybackService(core, DispatcherQueue);
            playback.Volume = volumeSlider.Value / 100;
            playback.PropertyChanged += PlaybackChanged;
            playback.Queue.CollectionChanged += (_, _) => { if (panelMode == "queue" && sidePanel.Visibility == Visibility.Visible) RenderQueue(); };
            UpdateAccountButton(); maintenanceTimer.Start();
            await NavigateAsync("home", new() { ["op"] = "home" }, "好音乐，慢慢听。", false);
            if (core.SessionStatus is "rejected" or "restore_failed") ShowNotice("保存的会话暂不可用，请重新导入。你仍可以搜索公开音乐。", InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); RenderWelcome(); }
        finally { loading.Visibility = Visibility.Collapsed; }
    }

    private async Task NavigateAsync(string view, Dictionary<string, object?> request, string title, bool push = true)
    {
        if (core is null || closing) return;
        pageLoad?.Cancel(); pageLoad?.Dispose(); pageLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = pageLoad.Token;
        if (push) history.Push((currentView, currentRequest, currentTitle));
        currentView = view; currentRequest = new(request); currentTitle = title; currentPage = null;
        backButton.IsEnabled = history.Count > 0; notice.IsOpen = false; loading.Visibility = Visibility.Visible;
        pageBody.Children.Clear(); pageBody.Children.Add(Heading(title, "正在加载…", Eyebrow(view)));
        pageScroll.ChangeView(null, 0, null);
        try
        {
            JsonElement result = await core.CallAsync(request, token);
            token.ThrowIfCancellationRequested();
            currentPage = MusicPage.FromJson(result);
            RenderPage(result);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (!token.IsCancellationRequested) pageScroll.ChangeView(null, 0, null, true); });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            pageBody.Children.Clear(); pageBody.Children.Add(Heading(title, "暂时无法加载此页面", Eyebrow(view)));
            var retry = ActionButton("重新加载", async () => await NavigateAsync(view, request, title, false), true);
            pageBody.Children.Add(retry);
            if (core.IsAuthenticated == false && (view == "library" || view == "likes" || view == "home")) RenderSignInCard();
            ShowError(error);
        }
        finally { if (!token.IsCancellationRequested) loading.Visibility = Visibility.Collapsed; }
    }

    private void RenderPage(JsonElement result)
    {
        if (currentPage is null) return;
        pageBody.Children.Clear();
        string subtitle = currentView switch { "home" => "从一首喜欢的歌，开始今天。", "explore" => "熟悉的旋律之外，还有新的风景。", "library" => "把每一次心动，好好收藏。", "likes" => "所有让你按下喜欢的瞬间，都在这里。", "search" => $"搜索结果 · {currentPage.Items.Count} 项", _ => $"{currentPage.Items.Count} 项" };
        pageBody.Children.Add(Heading(currentView == "browse" || currentView == "playlist" ? string.IsNullOrWhiteSpace(currentPage.Title) ? currentTitle : currentPage.Title : currentTitle, subtitle, Eyebrow(currentView)));
        if (currentView == "library") AddLibraryFilters();
        if (currentView == "search") AddSearchFilters();
        if (result.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var filter in filters.EnumerateArray())
            {
                string title = JsonString(filter, "title") ?? ""; string? param = JsonString(filter, "params");
                if (string.IsNullOrWhiteSpace(title)) continue;
                var button = new ToggleButton { Content = title, IsChecked = filter.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True };
                button.Click += async (_, _) => await NavigateAsync(currentView, new() { ["op"] = currentView == "explore" ? "explore" : "home", ["params"] = param }, currentTitle, false);
                chips.Children.Add(button);
            }
            if (chips.Children.Count > 0) pageBody.Children.Add(HorizontalScroll(chips));
        }
        if (currentView is "home" or "explore")
        {
            var feature = currentPage.Items.FirstOrDefault(item => item.ImageUrl is not null && (item.BrowseId is not null || item.VideoId is not null));
            if (feature is not null) pageBody.Children.Add(FeatureCard(feature));
        }
        if (currentView is "playlist" or "browse" or "likes") AddPageActions();
        if (currentPage.Items.Count == 0)
        {
            var empty = new StackPanel { Spacing = 16, Margin = new Thickness(15, 40, 15, 40), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(Icon("\uE8D6", 36, Muted)); empty.Children.Add(Text("这里还没有音乐", 18));
            empty.Children.Add(Text("试试搜索喜欢的歌曲，或选择另一个分类。", 12, Muted)); pageBody.Children.Add(empty);
        }
        foreach (var section in currentPage.Sections) AddSection(section, currentView is "playlist" or "likes" || currentView == "library" && selectedLibrary == "songs");
        if (!string.IsNullOrEmpty(currentPage.Continuation))
        {
            var request = new Dictionary<string, object?>(currentRequest) { ["continuation"] = currentPage.Continuation };
            Button? more = null; more = ActionButton("加载更多推荐", () => AppendPageAsync(request, more), false);
            pageBody.Children.Add(more);
        }
        var footer = Text("ytmusicwinui  ·  YOUR MUSIC, AT YOUR PACE.", 10, Muted); footer.Margin = new Thickness(0, 14, 0, 0); pageBody.Children.Add(footer);
    }

    private void AddSection(MusicSection section, bool forceRows = false)
    {
        if (section.Items.Count == 0) return;
        var block = new StackPanel { Spacing = 13 };
        if (!string.IsNullOrWhiteSpace(section.Title)) block.Children.Add(Text(section.Title, 19));
        bool rows = forceRows || section.Items.All(i => i.VideoId is not null);
        block.Children.Add(rows ? TrackList(section.Items) : CardList(section.Items));
        if (!string.IsNullOrEmpty(section.Continuation))
        {
            Button? more = null;
            string endpoint = currentView == "search" ? "search" : "browse";
            more = ActionButton("加载更多", async () =>
            {
                var request = new Dictionary<string, object?> { ["op"] = "continue", ["endpoint"] = endpoint, ["token"] = section.Continuation };
                await AppendPageAsync(request, more);
            }, false);
            block.Children.Add(more);
        }
        pageBody.Children.Add(block);
    }

    private async Task AppendPageAsync(Dictionary<string, object?> request, Button? trigger = null)
    {
        if (core is null || pageLoad is null) return;
        var token = pageLoad.Token;
        if (trigger is not null) trigger.IsEnabled = false;
        try
        {
            var more = MusicPage.FromJson(await core.CallAsync(request, token)); token.ThrowIfCancellationRequested();
            if (trigger?.Parent is Panel parent) parent.Children.Remove(trigger);
            if (currentPage is not null)
                currentPage = new MusicPage { Title = currentPage.Title, PlaylistId = currentPage.PlaylistId, Actions = currentPage.Actions,
                    Sections = currentPage.Sections.Concat(more.Sections).ToArray(), Items = currentPage.Items.Concat(more.Items).ToArray(), Continuation = more.Continuation };
            foreach (var section in more.Sections) AddSection(section, currentView is "playlist" or "likes");
            if (more.Continuation is not null && request.TryGetValue("op", out var op) && op is "home" or "explore")
            {
                var next = new Dictionary<string, object?>(request) { ["continuation"] = more.Continuation };
                Button? button = null; button = ActionButton("加载更多推荐", () => AppendPageAsync(next, button), false); pageBody.Children.Add(button);
            }
            if (more.Items.Count == 0) ShowNotice("已加载全部内容", InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowError(error); }
        finally { if (trigger is not null) trigger.IsEnabled = true; }
    }

    private void GoBack()
    {
        if (history.TryPop(out var entry))
        {
            if (entry.View == "settings") ShowSettings(false);
            else if (entry.View == "recent") ShowRecent(false);
            else _ = NavigateAsync(entry.View, entry.Request, entry.Title, false);
        }
        backButton.IsEnabled = history.Count > 0;
    }

    private async Task LoadLibraryAsync(string section)
    {
        selectedLibrary = section;
        await NavigateAsync("library", new() { ["op"] = "library", ["section"] = section }, "你的音乐，自成一片。");
    }
    private void AddLibraryFilters()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (name, key) in new[] { ("歌单", "playlists"), ("歌曲", "songs"), ("专辑", "albums"), ("艺人", "artists"), ("订阅", "subscriptions") })
        {
            var button = new ToggleButton { Content = name, IsChecked = selectedLibrary == key };
            button.Click += async (_, _) => await LoadLibraryAsync(key); row.Children.Add(button);
        }
        row.Children.Add(ActionButton("＋ 新建歌单", ShowCreatePlaylistAsync, false)); pageBody.Children.Add(HorizontalScroll(row));
    }
    private void AddSearchFilters()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (name, key) in new[] { ("全部", "all"), ("歌曲", "songs"), ("视频", "videos"), ("专辑", "albums"), ("艺人", "artists"), ("歌单", "playlists") })
        {
            var button = new ToggleButton { Content = name, IsChecked = searchFilter == key };
            button.Click += async (_, _) => { searchFilter = key; await NavigateAsync("search", new() { ["op"] = "search", ["query"] = currentRequest["query"], ["filter"] = key }, currentTitle, false); }; row.Children.Add(button);
        }
        pageBody.Children.Add(HorizontalScroll(row));
    }
    private async void SearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || core is null) return;
        suggestionLoad?.Cancel(); suggestionLoad?.Dispose(); suggestionLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = suggestionLoad.Token; string query = sender.Text.Trim();
        if (query.Length == 0) { sender.ItemsSource = null; return; }
        try
        {
            await Task.Delay(350, token);
            var result = await core.CallAsync(new { op = "search_suggestions", query }, token); token.ThrowIfCancellationRequested();
            var values = result.ValueKind == JsonValueKind.Array ? result : result.TryGetProperty("suggestions", out var list) ? list : default;
            if (values.ValueKind == JsonValueKind.Array) sender.ItemsSource = values.EnumerateArray().Select(v => JsonString(v, "query")).Where(v => !string.IsNullOrEmpty(v)).Take(8).ToArray();
        }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested) sender.ItemsSource = null; }
    }

    private async Task OpenItemAsync(MusicItem item, IEnumerable<MusicItem>? context = null)
    {
        if (item.VideoId is not null)
        {
            if (item.Available == false) { ShowNotice("这首歌曲目前不可播放", InfoBarSeverity.Warning); return; }
            if (playback is not null) await playback.PlayAsync(item, context);
        }
        else if (item.PlaylistId is not null) await NavigateAsync("playlist", new() { ["op"] = "playlist", ["playlist_id"] = item.PlaylistId }, item.Title);
        else if (item.BrowseId is not null) await NavigateAsync("browse", new() { ["op"] = "browse", ["browse_id"] = item.BrowseId }, item.Title);
    }
    private void ShowRecent(bool push = true)
    {
        if (push) history.Push((currentView, currentRequest, currentTitle));
        pageLoad?.Cancel(); currentPage = null; currentView = "recent"; currentTitle = "让旋律，再来一遍。"; loading.Visibility = Visibility.Collapsed; backButton.IsEnabled = history.Count > 0;
        pageBody.Children.Clear(); pageBody.Children.Add(Heading(currentTitle, "本次打开应用时听过的音乐", "RECENTLY PLAYED"));
        if (recent.Count == 0) pageBody.Children.Add(Text("播放一首歌，它就会出现在这里。", 14, Muted)); else pageBody.Children.Add(TrackList(recent));
    }
    private void RenderWelcome()
    {
        pageBody.Children.Clear(); pageBody.Children.Add(Heading("欢迎使用 ytmusicwinui", "搜索公开音乐，或导入账户查看个人音乐库。", "YOUR MUSIC, YOUR MOMENT"));
        pageBody.Children.Add(ActionButton("重试连接", InitializeAsync, true));
    }
    private void RenderSignInCard()
    {
        var content = new StackPanel { Spacing = 15 };
        content.Children.Add(Text("把你的音乐，带到这里。", 22)); content.Children.Add(Text("导入已登录的 YouTube Music 会话，即可查看歌单和收藏。", 13, Muted));
        content.Children.Add(ActionButton("导入账户", ShowImportDialogAsync, true));
        pageBody.Children.Add(new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(26), Child = content });
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (closing) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error)
    {
        DiagnosticLog.Write("operation_failure", error); UpdateAccountButton();
        string message = error is MusicCoreException music ? music.Code switch
        {
            "authentication_required" => "请先导入账户，再访问你的音乐库。",
            "authentication_rejected" => "账户会话已失效，请重新导入已登录的会话。",
            "timeout" => "连接超时，请稍后重试。首次准备播放可能需要更长时间。",
            "rate_limited" => "请求暂时过于频繁，请稍后再试。",
            "mutation_outcome_unknown" => "服务器可能已保存这次修改。请刷新核对，不要重复提交。",
            "po_token_required" or "attestation_required" => "这首歌曲需要额外的网页验证，当前原生客户端暂时无法播放。",
            "lyrics_unavailable" => "这首歌曲暂时没有可用歌词。",
            "network" or "http" => "网络暂时不可用，请检查连接后重试。",
            _ => $"操作未完成（{SafeCode(music.Code)}）。请刷新页面后重试。"
        } : error is DllNotFoundException or BadImageFormatException ? "未找到匹配的音乐核心，请使用完整的 Windows 发布包。" : "操作未完成，请稍后重试。";
        ShowNotice(message, InfoBarSeverity.Warning);
    }
    private static string SafeCode(string code) => code.Length <= 60 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "unknown";
    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) { if (closing) return; notice.Message = message; notice.Severity = severity; notice.IsOpen = true; }
    private void UpdateAccountButton() => accountButton.Content = Horizontal(Icon("\uE77B", 18), Text(core?.IsAuthenticated == true ? core.AccountName : "导入账户", 12));
    private async Task MaintainSessionAsync()
    {
        if (core?.IsAuthenticated != true || refreshingSession || closing || playback?.IsBusy == true) return;
        refreshingSession = true;
        try { await core.MaintainSessionAsync(lifetime.Token); UpdateAccountButton(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
        finally { refreshingSession = false; }
    }
    private async Task DisposeServicesAsync()
    {
        if (closing) return; closing = true; maintenanceTimer.Stop(); lifetime.Cancel(); pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel();
        try { if (playback is not null) await playback.DisposeAsync(); if (core is not null) await core.DisposeAsync(); }
        catch (Exception error) { DiagnosticLog.Write("shutdown_failure", error); }
    }

    private static readonly SolidColorBrush BackgroundBrush = Brush(244, 246, 242), Card = Brush(255, 255, 255), Ink = Brush(38, 59, 49), Muted = Brush(122, 135, 126), Line = Brush(223, 229, 222), Accent = Brush(36, 87, 67), White = Brush(240, 246, 237);
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private static TextBlock Text(string value, double size = 14, Brush? brush = null) => new() { Text = value, FontSize = size, Foreground = brush ?? Ink, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static FontIcon Icon(string glyph, double size = 18, Brush? brush = null) => new() { Glyph = glyph, FontSize = size, Foreground = brush ?? Ink, FontFamily = new FontFamily("Segoe Fluent Icons"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private static Button IconButton(string glyph, string label) { var b = new Button { Content = Icon(glyph), Width = 36, Height = 34, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8) }; AutomationProperties.SetName(b, label); ToolTipService.SetToolTip(b, label); return b; }
    private static ToggleButton ToggleIcon(string glyph, string label) { var b = new ToggleButton { Content = Icon(glyph), Width = 34, Height = 32, Padding = new Thickness(0), BorderThickness = new Thickness(0) }; AutomationProperties.SetName(b, label); ToolTipService.SetToolTip(b, label); return b; }
    private Button ActionButton(string label, Func<Task> action, bool accent = false) { var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left }; if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; b.Click += async (_, _) => { b.IsEnabled = false; try { await GuardAsync(action); } finally { b.IsEnabled = true; } }; return b; }
    private static StackPanel Horizontal(params UIElement[] children) { var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, VerticalAlignment = VerticalAlignment.Center }; foreach (var child in children) panel.Children.Add(child); return panel; }
    private static ScrollViewer HorizontalScroll(UIElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    private static NavigationViewItem NavItem(string label, string glyph, string key) { var item = new NavigationViewItem { Content = label, Icon = new FontIcon { Glyph = glyph }, Tag = key }; AutomationProperties.SetAutomationId(item, "Nav-" + key); return item; }
    private static string? JsonString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Eyebrow(string view) => view switch { "home" => "A LITTLE MUSIC. A BETTER DAY.", "explore" => "A LITTLE OUTSIDE YOUR USUAL", "library" => "YOUR OWN LITTLE UNIVERSE", "search" => "FIND YOUR NEXT FAVORITE", _ => "YOUR MUSIC, YOUR MOMENT" };
    private static StackPanel Heading(string title, string subtitle, string eyebrow) { var p = new StackPanel { Spacing = 9 }; var eye = Text(eyebrow, 10, Muted); eye.CharacterSpacing = 160; p.Children.Add(eye); p.Children.Add(Text(title, 30)); p.Children.Add(Text(subtitle, 12, Muted)); return p; }
    private static void SetImage(Image image, string? url) { image.Source = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "ms-appx") ? new BitmapImage(uri) : null; }
}
