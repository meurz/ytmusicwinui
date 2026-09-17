using Music.Desktop.Localization;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Music.Desktop.Models;
using Windows.System;

namespace Music.Desktop;
public sealed partial class MainWindow
{
    private async Task<bool> RequireAccountAsync()
    {
        if (core?.IsAuthenticated == true) return true;
        await ShowImportDialogAsync();
        return core?.IsAuthenticated == true;
    }
    private ContentDialog NewDialog(string title, object content, string? primary = null, string? close = null) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary ?? L.Get("Confirm"), CloseButtonText = close ?? L.Get("Cancel"),
        DefaultButton = ContentDialogButton.Primary, XamlRoot = ((FrameworkElement)Content).XamlRoot, RequestedTheme = ElementTheme.Light
    };
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialogOpen || closing) return ContentDialogResult.None;
        dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
    }
    private async Task ShowImportDialogAsync()
    {
        if (core is null) { ShowNotice(L.Get("CoreNotReady"), InfoBarSeverity.Warning); return; }
        var body = new StackPanel { Spacing = 15, MinWidth = 350, MaxWidth = 440 };
        body.Children.Add(Text(L.Get("ImportAccountExplanation"), 13, Muted));
        body.Children.Add(ActionButton(L.Get("OpenYouTubeMusic"), async () => { await Launcher.LaunchUriAsync(new Uri("https://music.youtube.com/")); }));
        body.Children.Add(Text(L.Get("CookieImportInstructions"), 12, Muted));
        var input = new PasswordBox { PlaceholderText = L.Get("CookiePlaceholder"), PasswordRevealMode = PasswordRevealMode.Hidden, MaxLength = 65536 };
        body.Children.Add(input); body.Children.Add(Text(L.Get("SessionStorageExplanation"), 11, Muted));
        var status = Text("", 12, Accent); body.Children.Add(status);
        var dialog = NewDialog(L.Get("ImportMusicAccount"), body, L.Get("VerifyAndImport"));
        bool importing = false, imported = false;
        dialog.Closing += (_, args) => { if (importing) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (string.IsNullOrWhiteSpace(input.Password)) { status.Text = L.Get("CookieRequired"); return; }
            var deferral = args.GetDeferral(); importing = true; dialog.IsPrimaryButtonEnabled = false; input.IsEnabled = false; status.Text = L.Get("VerifyingAccount");
            try
            {
                pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel();
                if (playback is not null) await playback.StopAsync();
                await core.ImportCookieAsync(input.Password, lifetime.Token);
                input.Password = ""; imported = true; args.Cancel = false;
            }
            catch (OperationCanceledException) { status.Text = L.Get("ImportCancelled"); }
            catch (Exception error) { DiagnosticLog.Write("session_import", error); status.Text = L.Get("ImportFailed"); }
            finally { importing = false; input.IsEnabled = true; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        try { await ShowDialogAsync(dialog); }
        finally { input.Password = ""; }
        if (imported) { ResetAccountView(); await NavigateAsync("home", new() { ["op"] = "home" }, L.Get("HomeTitle"), false); ShowNotice(L.Get("AccountImported")); }
    }
    private async Task ShowAccountDialogAsync()
    {
        if (core is null) return;
        await GuardAsync(async () =>
        {
            var result = await core.CallAsync(new { op = "accounts" }, lifetime.Token);
            var accounts = result.ValueKind == JsonValueKind.Array ? result : result.TryGetProperty("accounts", out var array) ? array : default;
            var body = new StackPanel { Spacing = 15, MinWidth = 340 };
            body.Children.Add(Text(L.Get("CurrentAccountFormat", core.AccountName), 14));
            var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = L.Get("SelectMusicProfile") };
            if (accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    var candidateSelector = account.TryGetProperty("selector", out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : default;
                    bool disabled = account.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
                    var item = new ComboBoxItem { Content = JsonString(account, "name") ?? JsonString(account, "channel_handle") ?? L.Get("MusicAccount"), Tag = candidateSelector, IsEnabled = !disabled && candidateSelector.ValueKind == JsonValueKind.Object };
                    choices.Items.Add(item);
                    if (account.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True) choices.SelectedItem = item;
                }
            }
            body.Children.Add(choices); body.Children.Add(Text(L.Get("ProfileUnavailableExplanation"), 12, Muted));
            var dialog = NewDialog(L.Get("Account"), body, L.Get("SwitchProfile"), L.Get("Return"));
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && choices.SelectedItem is ComboBoxItem { Tag: JsonElement selector } && selector.ValueKind == JsonValueKind.Object)
            {
                pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel(); if (playback is not null) await playback.StopAsync();
                await core.SelectAccountAsync(selector, lifetime.Token); ResetAccountView(); await NavigateAsync("home", new() { ["op"] = "home" }, L.Get("HomeTitle"), false);
            }
        });
    }
    private void ResetAccountView()
    {
        recent.Clear(); history.Clear(); currentPage = null; loadedLyricsVideo = null; observedVideo = null; playerRating = null;
        ClosePanel(); UpdateAccountButton(); trackTitle.Text = L.Get("SelectSongToPlay"); trackArtist.Text = "ytmusicwinui"; playerCover.Source = null; likeButton.IsEnabled = false;
    }
    private async Task SignOutAsync()
    {
        if (core is null) return;
        var dialog = NewDialog(L.Get("SignOutCurrentAccount"), Text(L.Get("SignOutExplanation"), 13, Muted), L.Get("SignOutAccount"));
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel(); if (playback is not null) await playback.StopAsync();
        await core.SignOutAsync(lifetime.Token); ResetAccountView(); ShowSettings(false); ShowNotice(L.Get("SignedOut"));
    }
    private void ShowSettings(bool push = true)
    {
        if (push) history.Push((currentView, currentRequest, currentTitle));
        pageLoad?.Cancel(); currentPage = null; currentView = "settings"; currentTitle = L.Get("Settings"); backButton.IsEnabled = history.Count > 0; loading.Visibility = Visibility.Collapsed;
        pageBody.Children.Clear(); pageBody.Children.Add(Heading(L.Get("SettingsTitle"), L.Get("SettingsDescription"), L.Get("EyebrowSettings")));
        var account = new StackPanel { Spacing = 10 };
        account.Children.Add(Text(L.Get("Account"), 18));
        account.Children.Add(new SettingsCard { Header = core?.IsAuthenticated == true ? core.AccountName : L.Get("NotSignedIn"), Description = L.Get("ImportSessionDescription"), HeaderIcon = Icon("\uE77B"), Content = ActionButton(L.Get("ImportSession"), ShowImportDialogAsync) });
        if (core?.IsAuthenticated == true)
        {
            account.Children.Add(new SettingsCard { Header = L.Get("MusicProfile"), Description = L.Get("MusicProfileDescription"), HeaderIcon = Icon("\uE8D4"), Content = ActionButton(L.Get("Manage"), ShowAccountDialogAsync) });
            account.Children.Add(new SettingsCard { Header = L.Get("SessionMaintenance"), Description = L.Get("SessionMaintenanceDescription"), HeaderIcon = Icon("\uE72C"), Content = ActionButton(L.Get("RefreshNow"), async () => { if (core is not null) { await core.MaintainSessionAsync(lifetime.Token); UpdateAccountButton(); ShowNotice(L.Get("SessionRefreshed")); } }) });
        }
        if (core?.SessionStatus is "authenticated" or "rejected" or "restore_failed")
            account.Children.Add(new SettingsCard { Header = L.Get("ClearLocalSession"), Description = L.Get("ClearSessionDescription"), HeaderIcon = Icon("\uE8AC"), Content = ActionButton(L.Get("SignOut"), SignOutAsync) });
        pageBody.Children.Add(account);
        var languages = new ComboBox { MinWidth = 190 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(languages, "LanguageSelector");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(languages, L.Get("Language"));
        foreach (var (tag, label) in new[] { ("system", L.Get("FollowSystem")), ("en-US", "English"), ("zh-CN", "简体中文"), ("zh-TW", "繁體中文") })
        {
            var choice = new ComboBoxItem { Content = label, Tag = tag };
            languages.Items.Add(choice);
            if (tag == L.SelectedLanguage) languages.SelectedItem = choice;
        }
        languages.SelectionChanged += (_, _) =>
        {
            if (languages.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            try { L.SaveLanguage(tag); ShowNotice(L.Get("LanguageSaved")); }
            catch (Exception error) { ShowError(error); }
        };
        pageBody.Children.Add(new SettingsCard { Header = L.Get("Language"), Description = L.Get("LanguageDescription"), HeaderIcon = Icon("\uE774"), Content = languages });
        var audio = new StackPanel { Spacing = 10 }; audio.Children.Add(Text(L.Get("Playback"), 18));
        audio.Children.Add(new SettingsCard { Header = L.Get("WindowsNativeAudio"), Description = L.Get("PlaybackFeatures"), HeaderIcon = Icon("\uE8D6"), Content = Text(L.Get("Automatic"), 12, Muted) });
        audio.Children.Add(new SettingsCard { Header = L.Get("PlaybackQueue"), Description = L.Get("QueueDescription"), HeaderIcon = Icon("\uE8FD"), Content = ActionButton(L.Get("OpenQueue"), () => { lyricsLoad?.Cancel(); panelMode = "queue"; queueButton.IsChecked = true; sidePanel.Visibility = Visibility.Visible; RenderQueue(); return Task.CompletedTask; }) });
        pageBody.Children.Add(audio);
        var about = new StackPanel { Spacing = 10 }; about.Children.Add(Text(L.Get("About"), 18));
        about.Children.Add(new SettingsCard { Header = "ytmusicwinui", Description = $"0.1.2 · Music Core {core?.CoreVersion ?? "—"}", HeaderIcon = Icon("\uE946"), Content = ActionButton("GitHub", async () => { await Launcher.LaunchUriAsync(new Uri("https://github.com/meurz/ytmusicwinui")); }) });
        about.Children.Add(Text(L.Get("UnofficialDisclaimer"), 11, Muted)); pageBody.Children.Add(about);
    }
    private async Task ShowCreatePlaylistAsync()
    {
        if (!await RequireAccountAsync() || core is null) return;
        var title = new TextBox { PlaceholderText = L.Get("PlaylistTitlePlaceholder"), MaxLength = 150 };
        var description = new TextBox { PlaceholderText = L.Get("PlaylistDescriptionPlaceholder"), AcceptsReturn = true, Height = 75, MaxLength = 1000 };
        var body = new StackPanel { Spacing = 12, MinWidth = 350 }; body.Children.Add(Text(L.Get("NewPlaylistDescription"), 13, Muted)); body.Children.Add(title); body.Children.Add(description); body.Children.Add(Text(L.Get("PrivatePlaylistNotice"), 11, Muted));
        var dialog = NewDialog(L.Get("NewPlaylist"), body, L.Get("Create"));
        dialog.IsPrimaryButtonEnabled = false; title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var response = await core.CallAsync(new { op = "create_playlist", title = title.Text.Trim(), description = description.Text.Trim(), privacy = "private", video_ids = Array.Empty<string>() }, lifetime.Token);
        string? id = JsonString(response, "playlist_id");
        if (id is not null) await NavigateAsync("playlist", new() { ["op"] = "playlist", ["playlist_id"] = id }, title.Text.Trim());
        else await LoadLibraryAsync("playlists");
        ShowNotice(L.Get("PlaylistCreated"));
    }
    private async Task ShowEditPlaylistAsync()
    {
        string? id = currentPage?.PlaylistId; if (id is null || core is null) return;
        var title = new TextBox { Text = currentPage!.Title, MaxLength = 150, MinWidth = 340 };
        var dialog = NewDialog(L.Get("RenamePlaylist"), title, L.Get("Save")); title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await MutateAndReloadAsync(new { op = "edit_playlist", playlist_id = id, title = title.Text.Trim() });
    }
    private async Task ShowDeletePlaylistAsync()
    {
        string? id = currentPage?.PlaylistId; if (id is null || core is null) return;
        var dialog = NewDialog(L.Get("DeletePlaylist"), Text(L.Get("DeletePlaylistConfirmation", currentPage!.Title), 13), L.Get("Delete"));
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await core.CallAsync(new { op = "delete_playlist", playlist_id = id }, lifetime.Token); await LoadLibraryAsync("playlists"); ShowNotice(L.Get("PlaylistDeleted"));
    }
    private async Task ShowAddToPlaylistAsync(MusicItem song)
    {
        if (!await RequireAccountAsync() || core is null || string.IsNullOrWhiteSpace(song.VideoId)) return;
        var client = core;
        using var dialogLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var dialogToken = dialogLoad.Token;
        var choices = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsItemClickEnabled = false
        };
        ScrollViewer.SetVerticalScrollMode(choices, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(choices, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(choices, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(choices, ScrollBarVisibility.Disabled);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choices, L.Get("SelectPlaylist"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(choices, "AddToPlaylistChoices");
        var progress = new ProgressRing { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(progress, L.Get("Loading"));
        var scrollBody = new StackPanel { Spacing = 8 };
        scrollBody.Children.Add(choices); scrollBody.Children.Add(progress);
        var viewport = new ScrollViewer
        {
            Content = scrollBody, Height = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(viewport, "AddToPlaylistScroll");
        var body = new StackPanel { Spacing = 12, MinWidth = 350, MaxWidth = 440 };
        body.Children.Add(Text(song.Title, 16)); body.Children.Add(Text(L.Get("SelectPlaylist"), 12, Muted)); body.Children.Add(viewport);
        var help = Text(L.Get("Loading"), 11, Muted); body.Children.Add(help);
        var dialog = NewDialog(L.Get("AddToPlaylist"), body, L.Get("Add"));
        dialog.IsPrimaryButtonEnabled = false;
        var tokens = new Queue<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var seenLists = new HashSet<string>(StringComparer.Ordinal);
        bool firstPageLoaded = false, isLoading = false, dialogClosed = false;
        string? selectedPlaylistId = null;
        int consecutiveEmptyPages = 0;
        bool TryGetSelection(out string? id)
        {
            id = choices.SelectedItem is ListViewItem { IsEnabled: true, Tag: string candidate }
                && choices.Items.Contains(choices.SelectedItem) && seenLists.Contains(candidate) ? candidate : null;
            return !string.IsNullOrWhiteSpace(id);
        }
        void Append(MusicPage page)
        {
            foreach (var item in page.Items)
            {
                string? id = item.PlaylistId ?? item.BrowseId;
                if (id?.StartsWith("VL", StringComparison.Ordinal) == true) id = id[2..];
                if (!string.IsNullOrWhiteSpace(id) && item.Actions.CanEdit != false && seenLists.Add(id))
                    choices.Items.Add(new ListViewItem { Content = item.Title, Tag = id });
            }
            if (!string.IsNullOrWhiteSpace(page.Continuation) && seenTokens.Add(page.Continuation))
                tokens.Enqueue(page.Continuation);
            foreach (var section in page.Sections)
                if (!string.IsNullOrWhiteSpace(section.Continuation) && seenTokens.Add(section.Continuation))
                    tokens.Enqueue(section.Continuation);
            help.Text = choices.Items.Count > 0 ? L.Get("EditablePlaylistNotice")
                : tokens.Count > 0 ? L.Get("Loading") : L.Get("NoPlaylistsAvailable");
            dialog.IsPrimaryButtonEnabled = TryGetSelection(out _);
        }
        using var prefetch = new Services.ScrollPrefetcher(viewport, dialogToken, error =>
        {
            if (dialogClosed || dialogToken.IsCancellationRequested) return;
            DiagnosticLog.Write("playlist_picker_prefetch", error);
            help.Text = L.Get("PageLoadFailed");
            progress.IsActive = false;
        });
        prefetch.Add(progress, async cancellation =>
        {
            if (dialogClosed || cancellation.IsCancellationRequested) return false;
            if (isLoading) return true;
            if (firstPageLoaded && tokens.Count == 0) return false;
            isLoading = true; progress.IsActive = true;
            try
            {
                MusicPage page;
                if (!firstPageLoaded)
                    page = MusicPage.FromJson(await client.CallAsync(new { op = "library", section = "playlists" }, cancellation));
                else
                    page = MusicPage.FromJson(await client.CallAsync(new { op = "continue", endpoint = "browse", token = tokens.Peek() }, cancellation));
                cancellation.ThrowIfCancellationRequested();
                if (dialogClosed) return false;
                if (firstPageLoaded) tokens.Dequeue();
                firstPageLoaded = true;
                int previousCount = choices.Items.Count;
                Append(page);
                consecutiveEmptyPages = choices.Items.Count > previousCount ? 0 : consecutiveEmptyPages + 1;
                bool hasMore = tokens.Count > 0;
                if (hasMore && consecutiveEmptyPages >= 3)
                {
                    DiagnosticLog.Write("playlist_picker_empty_pages", new InvalidOperationException());
                    help.Text = L.Get("PageLoadFailed");
                    hasMore = false;
                }
                progress.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
                return hasMore;
            }
            finally { isLoading = false; if (!dialogClosed) progress.IsActive = false; }
        });
        choices.SelectionChanged += (_, _) =>
        {
            if (!dialogClosed) dialog.IsPrimaryButtonEnabled = TryGetSelection(out _);
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = !TryGetSelection(out selectedPlaylistId);
        };
        void StopPrefetch()
        {
            if (dialogClosed) return;
            dialogClosed = true;
            prefetch.Dispose(); dialogLoad.Cancel();
        }
        dialog.Closed += (_, _) => StopPrefetch();
        ContentDialogResult result;
        try { result = await ShowDialogAsync(dialog); }
        finally { StopPrefetch(); }
        if (result == ContentDialogResult.Primary && selectedPlaylistId is string playlistId && seenLists.Contains(playlistId))
        {
            var target = MusicPage.FromJson(await client.CallAsync(new { op = "playlist", playlist_id = playlistId }, lifetime.Token));
            if (target.Actions.CanEdit != true) { ShowNotice(L.Get("PlaylistNotEditable"), InfoBarSeverity.Warning); return; }
            await client.CallAsync(new { op = "add_playlist_items", playlist_id = playlistId, video_ids = new[] { song.VideoId }, allow_duplicates = false }, lifetime.Token);
            ShowNotice(L.Get("AddedToPlaylist"));
        }
    }
    private async Task RemovePlaylistEntryAsync(MusicItem song)
    {
        if (core is null || currentPage?.PlaylistId is not string id || song.SetVideoId is null) return;
        var dialog = NewDialog(L.Get("RemoveSong"), Text(L.Get("RemoveSongConfirmation", song.Title), 13), L.Get("Remove"));
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await MutateAndReloadAsync(new { op = "remove_playlist_items", playlist_id = id, entries = new[] { new { video_id = song.VideoId, set_video_id = song.SetVideoId } } });
    }
}
