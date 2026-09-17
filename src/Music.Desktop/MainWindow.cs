using Music.Desktop.Localization;
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
    private readonly AutoSuggestBox search = new() { PlaceholderText = L.Get("SearchPlaceholder"), QueryIcon = new SymbolIcon(Symbol.Find), MaxWidth = 430, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(24, 0, 24, 12) };
    private readonly ProgressBar loading = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Height = 3 };
    private readonly Button accountButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button backButton = IconButton("\uE72B", L.Get("Back"));
    private readonly StackPanel panelBody = new() { Spacing = 18, Margin = new Thickness(22) };
    private readonly Border sidePanel = new() { Width = 305, Visibility = Visibility.Collapsed, Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly Stack<(string View, Dictionary<string, object?> Request, string Title)> history = new();
    private Dictionary<string, object?> currentRequest = new() { ["op"] = "home" };
    private string currentView = "home", currentTitle = L.Get("HomeTitle"), selectedLibrary = "playlists", searchFilter = "all", panelMode = "queue";
    private MusicPage? currentPage;
    private readonly List<MusicItem> recent = new();
    private string? loadedLyricsVideo;
    private readonly TextBlock lyricText = Text(L.Get("SelectSongForLyrics"), 17, Muted);
    private readonly TextBlock trackTitle = Text(L.Get("SelectSongToPlay"), 13);
    private readonly TextBlock trackArtist = Text("ytmusicwinui", 11, Muted);
    private readonly TextBlock playStatus = Text("", 10, Muted);
    private readonly TextBlock positionLabel = Text("0:00", 10, Muted), durationLabel = Text("0:00", 10, Muted);
    private readonly Image playerCover = new() { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
    private readonly Button playButton = IconButton("\uE768", L.Get("PlayPause")), likeButton = IconButton("\uEB51", L.Get("LikeCurrentSong"));
    private readonly ToggleButton shuffleButton = ToggleIcon("\uE8B1", L.Get("Shuffle")), repeatButton = ToggleIcon("\uE8EE", L.Get("RepeatMode")), queueButton = ToggleIcon("\uE8FD", L.Get("PlaybackQueue")), lyricsButton = ToggleIcon("\uE8A5", L.Get("Lyrics"));
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
        var layout = new Grid { Language = L.LanguageTag, Background = BackgroundBrush, KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden };
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
        var refresh = IconButton("\uE72C", L.Get("RefreshPage")); refresh.Click += async (_, _) => { if (currentView == "settings") ShowSettings(false); else if (currentView == "recent") ShowRecent(false); else await NavigateAsync(currentView, currentRequest, currentTitle, false); }; Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
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
        pageBody.Children.Add(Heading(L.Get("WelcomeBack"), L.Get("ConnectingMusic"), L.Get("EyebrowWelcome")));
        _ = InitializeAsync();
    }

    private void ConfigureNavigation()
    {
        navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        navigation.OpenPaneLength = 210; navigation.CompactPaneLength = 48; navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        navigation.Loaded += (_, _) => { if (navigation.SettingsItem is NavigationViewItem settings) settings.Content = L.Get("Settings"); };
        navigation.IsPaneToggleButtonVisible = false; navigation.IsSettingsVisible = true; navigation.AlwaysShowHeader = false;
        navigation.MenuItems.Add(NavItem(L.Get("Home"), "\uE80F", "home"));
        navigation.MenuItems.Add(NavItem(L.Get("Explore"), "\uE707", "explore"));
        navigation.MenuItems.Add(NavItem(L.Get("Library"), "\uE8F1", "library"));
        navigation.MenuItems.Add(new NavigationViewItemSeparator());
        navigation.MenuItems.Add(new NavigationViewItemHeader { Content = L.Get("MyCollection") });
        navigation.MenuItems.Add(NavItem(L.Get("LikedMusic"), "\uEB51", "likes"));
        navigation.MenuItems.Add(NavItem(L.Get("RecentlyPlayed"), "\uE81C", "recent"));
        navigation.SelectedItem = navigation.MenuItems[0];
        navigation.ItemInvoked += async (_, args) =>
        {
            if (args.IsSettingsInvoked) { ShowSettings(); return; }
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string view)
            {
                switch (view)
                {
                    case "home": await NavigateAsync(view, new() { ["op"] = "home" }, L.Get("HomeTitle")); break;
                    case "explore": await NavigateAsync(view, new() { ["op"] = "explore" }, L.Get("ExploreTitle")); break;
                    case "library": await LoadLibraryAsync(selectedLibrary); break;
                    case "likes": await NavigateAsync("likes", new() { ["op"] = "library", ["section"] = "likes" }, L.Get("LikedMusic")); break;
                    case "recent": ShowRecent(); break;
                }
            }
        };
        accountButton.Content = Horizontal(Icon("\uE77B", 18), Text(L.Get("ImportAccount"), 12));
        accountButton.Click += async (_, _) => { if (core?.IsAuthenticated == true) await ShowAccountDialogAsync(); else await ShowImportDialogAsync(); };
        accountButton.Margin = new Thickness(10, 12, 10, 18); accountButton.Background = Card;
        navigation.PaneFooter = accountButton;
    }

    private async Task InitializeAsync()
    {
        loading.Visibility = Visibility.Visible;
        try
        {
            core = await CoreService.CreateAsync(lifetime.Token, L.LanguageTag);
            if (closing) { await core.DisposeAsync(); return; }
            playback = new PlaybackService(core, DispatcherQueue, key => L.Get(key));
            playback.Volume = volumeSlider.Value / 100;
            playback.PropertyChanged += PlaybackChanged;
            playback.Queue.CollectionChanged += (_, _) => { if (panelMode == "queue" && sidePanel.Visibility == Visibility.Visible) RenderQueue(); };
            UpdateAccountButton(); maintenanceTimer.Start();
            await NavigateAsync("home", new() { ["op"] = "home" }, L.Get("HomeTitle"), false);
            if (core.SessionStatus is "rejected" or "restore_failed") ShowNotice(L.Get("SavedSessionUnavailable"), InfoBarSeverity.Warning);
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
        pageBody.Children.Clear(); pageBody.Children.Add(Heading(title, L.Get("Loading"), Eyebrow(view)));
        pageScroll.ChangeView(null, 0, null);
        try
        {
            JsonElement result = await core.CallAsync(request, token);
            token.ThrowIfCancellationRequested();
            currentPage = MusicPage.FromJson(result);
            pageScroll.Focus(FocusState.Programmatic);
            RenderPage(result);
            pageScroll.UpdateLayout();
            pageScroll.ChangeView(null, 0, null, true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (!token.IsCancellationRequested) pageScroll.ChangeView(null, 0, null, true); });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            pageBody.Children.Clear(); pageBody.Children.Add(Heading(title, L.Get("PageLoadFailed"), Eyebrow(view)));
            var retry = ActionButton(L.Get("Reload"), async () => await NavigateAsync(view, request, title, false), true);
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
        string subtitle = currentView switch { "home" => L.Get("HomeDescription"), "explore" => L.Get("ExploreDescription"), "library" => L.Get("LibraryDescription"), "likes" => L.Get("LikedMusicDescription"), "search" => L.Get("SearchResultCount", currentPage.Items.Count), _ => L.Get("ItemCount", currentPage.Items.Count) };
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
            empty.Children.Add(Icon("\uE8D6", 36, Muted)); empty.Children.Add(Text(L.Get("EmptyLibraryTitle"), 18));
            empty.Children.Add(Text(L.Get("EmptyLibraryDescription"), 12, Muted)); pageBody.Children.Add(empty);
        }
        foreach (var section in currentPage.Sections) AddSection(section, currentView is "playlist" or "likes" || currentView == "library" && selectedLibrary == "songs");
        if (!string.IsNullOrEmpty(currentPage.Continuation))
        {
            var request = new Dictionary<string, object?>(currentRequest) { ["continuation"] = currentPage.Continuation };
            Button? more = null; more = ActionButton(L.Get("LoadMoreRecommendations"), () => AppendPageAsync(request, more), false);
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
            more = ActionButton(L.Get("LoadMore"), async () =>
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
                Button? button = null; button = ActionButton(L.Get("LoadMoreRecommendations"), () => AppendPageAsync(next, button), false); pageBody.Children.Add(button);
            }
            if (more.Items.Count == 0) ShowNotice(L.Get("AllContentLoaded"), InfoBarSeverity.Informational);
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
        await NavigateAsync("library", new() { ["op"] = "library", ["section"] = section }, L.Get("LibraryTitle"));
    }
    private void AddLibraryFilters()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (name, key) in new[] { (L.Get("Playlists"), "playlists"), (L.Get("Songs"), "songs"), (L.Get("Albums"), "albums"), (L.Get("Artists"), "artists"), (L.Get("Subscriptions"), "subscriptions") })
        {
            var button = new ToggleButton { Content = name, IsChecked = selectedLibrary == key };
            button.Click += async (_, _) => await LoadLibraryAsync(key); row.Children.Add(button);
        }
        row.Children.Add(ActionButton(L.Get("NewPlaylistShortcut"), ShowCreatePlaylistAsync, false)); pageBody.Children.Add(HorizontalScroll(row));
    }
    private void AddSearchFilters()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (name, key) in new[] { (L.Get("All"), "all"), (L.Get("Songs"), "songs"), (L.Get("Videos"), "videos"), (L.Get("Albums"), "albums"), (L.Get("Artists"), "artists"), (L.Get("Playlists"), "playlists") })
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
            if (item.Available == false) { ShowNotice(L.Get("SongUnavailable"), InfoBarSeverity.Warning); return; }
            if (playback is not null) await playback.PlayAsync(item, context);
        }
        else if (item.PlaylistId is not null) await NavigateAsync("playlist", new() { ["op"] = "playlist", ["playlist_id"] = item.PlaylistId }, item.Title);
        else if (item.BrowseId is not null) await NavigateAsync("browse", new() { ["op"] = "browse", ["browse_id"] = item.BrowseId }, item.Title);
    }
    private void ShowRecent(bool push = true)
    {
        if (push) history.Push((currentView, currentRequest, currentTitle));
        pageLoad?.Cancel(); currentPage = null; currentView = "recent"; currentTitle = L.Get("RecentTitle"); loading.Visibility = Visibility.Collapsed; backButton.IsEnabled = history.Count > 0;
        pageBody.Children.Clear(); pageBody.Children.Add(Heading(currentTitle, L.Get("RecentDescription"), L.Get("EyebrowRecent")));
        if (recent.Count == 0) pageBody.Children.Add(Text(L.Get("RecentEmpty"), 14, Muted)); else pageBody.Children.Add(TrackList(recent));
    }
    private void RenderWelcome()
    {
        pageBody.Children.Clear(); pageBody.Children.Add(Heading(L.Get("WelcomeTitle"), L.Get("WelcomeDescription"), L.Get("EyebrowWelcome")));
        pageBody.Children.Add(ActionButton(L.Get("RetryConnection"), InitializeAsync, true));
    }
    private void RenderSignInCard()
    {
        var content = new StackPanel { Spacing = 15 };
        content.Children.Add(Text(L.Get("ImportCardTitle"), 22)); content.Children.Add(Text(L.Get("ImportCardDescription"), 13, Muted));
        content.Children.Add(ActionButton(L.Get("ImportAccount"), ShowImportDialogAsync, true));
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
            "authentication_required" => L.Get("AccountRequired"),
            "authentication_rejected" => L.Get("SessionExpired"),
            "timeout" => L.Get("ConnectionTimeout"),
            "rate_limited" => L.Get("RateLimited"),
            "mutation_outcome_unknown" => L.Get("MutationOutcomeUnknown"),
            "po_token_required" or "attestation_required" => L.Get("WebVerificationRequired"),
            "lyrics_unavailable" => L.Get("LyricsUnavailable"),
            "network" or "http" => L.Get("NetworkUnavailable"),
            _ => L.Get("OperationFailedCode", SafeCode(music.Code))
        } : error is DllNotFoundException or BadImageFormatException ? L.Get("CoreNotFound") : L.Get("OperationFailed");
        ShowNotice(message, InfoBarSeverity.Warning);
    }
    private static string SafeCode(string code) => code.Length <= 60 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "unknown";
    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational) { if (closing) return; notice.Message = message; notice.Severity = severity; notice.IsOpen = true; }
    private void UpdateAccountButton() => accountButton.Content = Horizontal(Icon("\uE77B", 18), Text(core?.IsAuthenticated == true ? core.AccountName : L.Get("ImportAccount"), 12));
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
    private static string Eyebrow(string view) => view switch { "home" => L.Get("EyebrowHome"), "explore" => L.Get("EyebrowExplore"), "library" => L.Get("EyebrowLibrary"), "search" => L.Get("EyebrowSearch"), _ => L.Get("EyebrowWelcome") };
    private static StackPanel Heading(string title, string subtitle, string eyebrow) { var p = new StackPanel { Spacing = 9 }; var eye = Text(eyebrow, 10, Muted); eye.CharacterSpacing = 160; p.Children.Add(eye); p.Children.Add(Text(title, 30)); p.Children.Add(Text(subtitle, 12, Muted)); return p; }
    private static void SetImage(Image image, string? url) { image.Source = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "ms-appx") ? new BitmapImage(uri) : null; }
}
