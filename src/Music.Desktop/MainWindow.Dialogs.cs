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
    private ContentDialog NewDialog(string title, object content, string primary = "确定", string close = "取消") => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = close,
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
        if (core is null) { ShowNotice("音乐核心尚未就绪，请稍后重试。", InfoBarSeverity.Warning); return; }
        var body = new StackPanel { Spacing = 15, MinWidth = 350, MaxWidth = 440 };
        body.Children.Add(Text("使用浏览器中已登录的 YouTube Music 账户。应用不会索取 Google 密码。", 13, Muted));
        body.Children.Add(ActionButton("打开 YouTube Music", async () => { await Launcher.LaunchUriAsync(new Uri("https://music.youtube.com/")); }));
        body.Children.Add(Text("在浏览器开发者工具的「网络」中，选择 music.youtube.com 请求，将请求头里的 Cookie 值粘贴到下方。", 12, Muted));
        var input = new PasswordBox { PlaceholderText = "粘贴 Cookie 值", PasswordRevealMode = PasswordRevealMode.Hidden, MaxLength = 65536 };
        body.Children.Add(input); body.Children.Add(Text("会话只保存在本机，使用 Windows 账户加密；登录失效后需要重新导入。", 11, Muted));
        var status = Text("", 12, Accent); body.Children.Add(status);
        var dialog = NewDialog("导入音乐账户", body, "验证并导入");
        bool importing = false, imported = false;
        dialog.Closing += (_, args) => { if (importing) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (string.IsNullOrWhiteSpace(input.Password)) { status.Text = "请先粘贴 Cookie 值。"; return; }
            var deferral = args.GetDeferral(); importing = true; dialog.IsPrimaryButtonEnabled = false; input.IsEnabled = false; status.Text = "正在验证账户并加密保存…";
            try
            {
                pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel();
                if (playback is not null) await playback.StopAsync();
                await core.ImportCookieAsync(input.Password, lifetime.Token);
                input.Password = ""; imported = true; args.Cancel = false;
            }
            catch (OperationCanceledException) { status.Text = "导入已取消。"; }
            catch (Exception error) { DiagnosticLog.Write("session_import", error); status.Text = "导入未完成。请确认 Cookie 来自已登录的 Music 页面且复制完整，再重试。"; }
            finally { importing = false; input.IsEnabled = true; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        try { await ShowDialogAsync(dialog); }
        finally { input.Password = ""; }
        if (imported) { ResetAccountView(); await NavigateAsync("home", new() { ["op"] = "home" }, "好音乐，慢慢听。", false); ShowNotice("账户已导入，会话将自动维护"); }
    }
    private async Task ShowAccountDialogAsync()
    {
        if (core is null) return;
        await GuardAsync(async () =>
        {
            var result = await core.CallAsync(new { op = "accounts" }, lifetime.Token);
            var accounts = result.ValueKind == JsonValueKind.Array ? result : result.TryGetProperty("accounts", out var array) ? array : default;
            var body = new StackPanel { Spacing = 15, MinWidth = 340 };
            body.Children.Add(Text("当前账户：" + core.AccountName, 14));
            var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择音乐资料" };
            if (accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    var candidateSelector = account.TryGetProperty("selector", out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : default;
                    bool disabled = account.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
                    var item = new ComboBoxItem { Content = JsonString(account, "name") ?? JsonString(account, "channel_handle") ?? "音乐账户", Tag = candidateSelector, IsEnabled = !disabled && candidateSelector.ValueKind == JsonValueKind.Object };
                    choices.Items.Add(item);
                    if (account.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True) choices.SelectedItem = item;
                }
            }
            body.Children.Add(choices); body.Children.Add(Text("无法切换的资料，请在浏览器中选中后重新导入。", 12, Muted));
            var dialog = NewDialog("账户", body, "切换资料", "返回");
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && choices.SelectedItem is ComboBoxItem { Tag: JsonElement selector } && selector.ValueKind == JsonValueKind.Object)
            {
                pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel(); if (playback is not null) await playback.StopAsync();
                await core.SelectAccountAsync(selector, lifetime.Token); ResetAccountView(); await NavigateAsync("home", new() { ["op"] = "home" }, "好音乐，慢慢听。", false);
            }
        });
    }
    private void ResetAccountView()
    {
        recent.Clear(); history.Clear(); currentPage = null; loadedLyricsVideo = null; observedVideo = null; playerRating = null;
        ClosePanel(); UpdateAccountButton(); trackTitle.Text = "选择一首歌，开始播放"; trackArtist.Text = "ytmusicwinui"; playerCover.Source = null; likeButton.IsEnabled = false;
    }
    private async Task SignOutAsync()
    {
        if (core is null) return;
        var dialog = NewDialog("退出当前账户", Text("清除这台电脑保存的音乐会话。浏览器中的 Google 登录不会受到影响。", 13, Muted), "退出账户");
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        pageLoad?.Cancel(); suggestionLoad?.Cancel(); lyricsLoad?.Cancel(); if (playback is not null) await playback.StopAsync();
        await core.SignOutAsync(lifetime.Token); ResetAccountView(); ShowSettings(false); ShowNotice("已退出账户");
    }
    private void ShowSettings(bool push = true)
    {
        if (push) history.Push((currentView, currentRequest, currentTitle));
        pageLoad?.Cancel(); currentPage = null; currentView = "settings"; currentTitle = "设置"; backButton.IsEnabled = history.Count > 0; loading.Visibility = Visibility.Collapsed;
        pageBody.Children.Clear(); pageBody.Children.Add(Heading("让音乐，更合你的习惯。", "账户、播放与应用信息", "MAKE YOURSELF AT HOME"));
        var account = new StackPanel { Spacing = 10 };
        account.Children.Add(Text("账户", 18));
        account.Children.Add(new SettingsCard { Header = core?.IsAuthenticated == true ? core.AccountName : "尚未登录", Description = "从已登录的官方网页导入音乐会话", HeaderIcon = Icon("\uE77B"), Content = ActionButton("导入会话", ShowImportDialogAsync) });
        if (core?.IsAuthenticated == true)
        {
            account.Children.Add(new SettingsCard { Header = "音乐资料", Description = "在当前会话可用的账户或频道之间切换", HeaderIcon = Icon("\uE8D4"), Content = ActionButton("管理", ShowAccountDialogAsync) });
            account.Children.Add(new SettingsCard { Header = "会话维护", Description = "使用期间每 10 分钟检查并保存有效会话的更新", HeaderIcon = Icon("\uE72C"), Content = ActionButton("立即刷新", async () => { if (core is not null) { await core.MaintainSessionAsync(lifetime.Token); UpdateAccountButton(); ShowNotice("会话已刷新并保存"); } }) });
        }
        if (core?.SessionStatus is "authenticated" or "rejected" or "restore_failed")
            account.Children.Add(new SettingsCard { Header = "清除本机会话", Description = "即使断网或登录失效，也可以移除保存的会话", HeaderIcon = Icon("\uE8AC"), Content = ActionButton("退出", SignOutAsync) });
        pageBody.Children.Add(account);
        var audio = new StackPanel { Spacing = 10 }; audio.Children.Add(Text("播放", 18));
        audio.Children.Add(new SettingsCard { Header = "Windows 原生音频", Description = "AAC 自适应播放 · 系统媒体键 · 队列与循环", HeaderIcon = Icon("\uE8D6"), Content = Text("自动", 12, Muted) });
        audio.Children.Add(new SettingsCard { Header = "播放队列", Description = "右键歌曲可加入队列；点击底部队列按钮查看", HeaderIcon = Icon("\uE8FD"), Content = ActionButton("打开队列", () => { lyricsLoad?.Cancel(); panelMode = "queue"; queueButton.IsChecked = true; sidePanel.Visibility = Visibility.Visible; RenderQueue(); return Task.CompletedTask; }) });
        pageBody.Children.Add(audio);
        var about = new StackPanel { Spacing = 10 }; about.Children.Add(Text("关于", 18));
        about.Children.Add(new SettingsCard { Header = "ytmusicwinui", Description = $"0.1.0 · Music Core {core?.CoreVersion ?? "—"}", HeaderIcon = Icon("\uE946"), Content = ActionButton("GitHub", async () => { await Launcher.LaunchUriAsync(new Uri("https://github.com/meurz/ytmusicwinui")); }) });
        about.Children.Add(Text("非官方客户端，与 Google 或 YouTube 无隶属关系。部分地区或受限歌曲可能无法播放。", 11, Muted)); pageBody.Children.Add(about);
    }
    private async Task ShowCreatePlaylistAsync()
    {
        if (!await RequireAccountAsync() || core is null) return;
        var title = new TextBox { PlaceholderText = "例如：周末散步", MaxLength = 150 };
        var description = new TextBox { PlaceholderText = "为歌单留一句话（可选）", AcceptsReturn = true, Height = 75, MaxLength = 1000 };
        var body = new StackPanel { Spacing = 12, MinWidth = 350 }; body.Children.Add(Text("给下一段旋律，起个名字。", 13, Muted)); body.Children.Add(title); body.Children.Add(description); body.Children.Add(Text("新歌单默认设为私享。", 11, Muted));
        var dialog = NewDialog("新建歌单", body, "创建");
        dialog.IsPrimaryButtonEnabled = false; title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var response = await core.CallAsync(new { op = "create_playlist", title = title.Text.Trim(), description = description.Text.Trim(), privacy = "private", video_ids = Array.Empty<string>() }, lifetime.Token);
        string? id = JsonString(response, "playlist_id");
        if (id is not null) await NavigateAsync("playlist", new() { ["op"] = "playlist", ["playlist_id"] = id }, title.Text.Trim());
        else await LoadLibraryAsync("playlists");
        ShowNotice("歌单已创建");
    }
    private async Task ShowEditPlaylistAsync()
    {
        string? id = currentPage?.PlaylistId; if (id is null || core is null) return;
        var title = new TextBox { Text = currentPage!.Title, MaxLength = 150, MinWidth = 340 };
        var dialog = NewDialog("修改歌单名称", title, "保存"); title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await MutateAndReloadAsync(new { op = "edit_playlist", playlist_id = id, title = title.Text.Trim() });
    }
    private async Task ShowDeletePlaylistAsync()
    {
        string? id = currentPage?.PlaylistId; if (id is null || core is null) return;
        var dialog = NewDialog("删除歌单", Text($"确定删除「{currentPage!.Title}」？此操作会同步到你的音乐账户。", 13), "删除");
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await core.CallAsync(new { op = "delete_playlist", playlist_id = id }, lifetime.Token); await LoadLibraryAsync("playlists"); ShowNotice("歌单已删除");
    }
    private async Task ShowAddToPlaylistAsync(MusicItem song)
    {
        if (!await RequireAccountAsync() || core is null) return;
        var first = MusicPage.FromJson(await core.CallAsync(new { op = "library", section = "playlists" }, lifetime.Token));
        var choices = new ComboBox { PlaceholderText = "选择歌单", MinWidth = 350, HorizontalAlignment = HorizontalAlignment.Stretch };
        var tokens = new Queue<string>(); var seenTokens = new HashSet<string>(); var seenLists = new HashSet<string>();
        void Append(MusicPage page)
        {
            foreach (var item in page.Items)
            {
                string? id = item.PlaylistId ?? item.BrowseId;
                if (id is not null && item.Actions.CanEdit != false && seenLists.Add(id)) choices.Items.Add(new ComboBoxItem { Content = item.Title, Tag = id });
            }
            foreach (var section in page.Sections) if (section.Continuation is string token && seenTokens.Add(token)) tokens.Enqueue(token);
        }
        Append(first);
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(Text(song.Title, 16)); body.Children.Add(choices);
        var help = Text("仅能添加到你有编辑权限的歌单。", 11, Muted); body.Children.Add(help);
        Button? moreButton = null;
        moreButton = ActionButton("加载更多歌单", async () =>
        {
            if (!tokens.TryPeek(out string? token)) return;
            var page = MusicPage.FromJson(await core.CallAsync(new { op = "continue", endpoint = "browse", token }, lifetime.Token)); tokens.Dequeue(); Append(page);
            moreButton!.Visibility = tokens.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        moreButton.Visibility = tokens.Count > 0 ? Visibility.Visible : Visibility.Collapsed; body.Children.Add(moreButton);
        if (choices.Items.Count == 0) help.Text = tokens.Count > 0 ? "这一页未找到可编辑歌单，可以继续加载。" : "尚无可用歌单，请先在音乐库中新建。";
        var dialog = NewDialog("添加到歌单", body, "添加"); dialog.IsPrimaryButtonEnabled = false; choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedItem is not null;
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && choices.SelectedItem is ComboBoxItem { Tag: string id })
        {
            var target = MusicPage.FromJson(await core.CallAsync(new { op = "playlist", playlist_id = id }, lifetime.Token));
            if (target.Actions.CanEdit != true) { ShowNotice("该歌单没有可用的编辑权限，请选择自己的歌单。", InfoBarSeverity.Warning); return; }
            await core.CallAsync(new { op = "add_playlist_items", playlist_id = id, video_ids = new[] { song.VideoId }, allow_duplicates = false }, lifetime.Token); ShowNotice("已添加到歌单");
        }
    }
    private async Task RemovePlaylistEntryAsync(MusicItem song)
    {
        if (core is null || currentPage?.PlaylistId is not string id || song.SetVideoId is null) return;
        var dialog = NewDialog("移除歌曲", Text($"从当前歌单移除「{song.Title}」？", 13), "移除");
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await MutateAndReloadAsync(new { op = "remove_playlist_items", playlist_id = id, entries = new[] { new { video_id = song.VideoId, set_video_id = song.SetVideoId } } });
    }
}
