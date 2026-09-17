using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Music.Desktop.Localization;
using Music.Desktop.Models;
using Music.Desktop.Services;

namespace Music.Desktop;

public sealed partial class MainWindow
{
    private ScrollPrefetcher? pagePrefetch;
    private TextBlock? pageCountLabel;
    private readonly StackPanel sectionBody = new() { Spacing = 25 };
    private readonly List<MusicSection> displayedSections = [];
    private readonly HashSet<string> registeredCursors = new(StringComparer.Ordinal);
    private bool UseTrackRows => currentView is "playlist" or "likes" || currentView == "library" && selectedLibrary == "songs";

    private void BeginPagePrefetch()
    {
        pagePrefetch?.Dispose();
        sectionBody.Children.Clear();
        displayedSections.Clear();
        registeredCursors.Clear();
        pagePrefetch = new ScrollPrefetcher(pageScroll, pageLoad!.Token, ShowError);
    }

    private static ProgressRing PrefetchIndicator()
    {
        var progress = new ProgressRing { IsActive = false, Width = 22, Height = 22, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(progress, L.Get("Loading"));
        return progress;
    }

    private void AddSection(MusicSection section, bool forceRows = false)
    {
        if (section.Items.Count == 0 && string.IsNullOrEmpty(section.Continuation)) return;
        var items = new ObservableCollection<MusicItem>(section.Items);
        displayedSections.Add(new MusicSection { Title = section.Title, Items = items });
        var block = new StackPanel { Spacing = 13 };
        if (!string.IsNullOrWhiteSpace(section.Title)) block.Children.Add(Text(section.Title, 19));
        bool rows = forceRows || section.Items.Count > 0 && section.Items.All(i => i.VideoId is not null);
        block.Children.Add(rows ? TrackList(items) : CardList(items));
        sectionBody.Children.Add(block);
        if (section.Continuation is not string first || !registeredCursors.Add(first)) return;

        var cursors = new Queue<string>();
        cursors.Enqueue(first);
        int emptyPages = 0;
        string endpoint = currentView == "search" ? "search" : "browse";
        var indicator = PrefetchIndicator();
        block.Children.Add(indicator);
        pagePrefetch!.Add(indicator, async cancellation =>
        {
            indicator.IsActive = true;
            try
            {
                var more = MusicPage.FromJson(await core!.CallAsync(new { op = "continue", endpoint, token = cursors.Peek() }, cancellation));
                cancellation.ThrowIfCancellationRequested();
                cursors.Dequeue();
                foreach (var item in more.Items) items.Add(item);
                foreach (var cursor in more.Sections.Select(section => section.Continuation).Append(more.Continuation))
                    if (!string.IsNullOrEmpty(cursor) && registeredCursors.Add(cursor)) cursors.Enqueue(cursor);
                emptyPages = more.Items.Count == 0 ? emptyPages + 1 : 0;
                RefreshPageItems();
                return cursors.Count > 0 && emptyPages < 3;
            }
            finally { indicator.IsActive = false; }
        });
    }

    private void AddFeedPrefetch(string? first)
    {
        if (string.IsNullOrEmpty(first) || !registeredCursors.Add(first)) return;
        string cursor = first;
        bool feed = currentView is "home" or "explore";
        string endpoint = currentView == "search" ? "search" : "browse";
        var original = new Dictionary<string, object?>(currentRequest);
        var indicator = PrefetchIndicator();
        pageBody.Children.Add(indicator);
        int emptyPages = 0;
        pagePrefetch!.Add(indicator, async cancellation =>
        {
            indicator.IsActive = true;
            try
            {
                var request = feed
                    ? new Dictionary<string, object?>(original) { ["continuation"] = cursor }
                    : new Dictionary<string, object?> { ["op"] = "continue", ["endpoint"] = endpoint, ["token"] = cursor };
                var more = MusicPage.FromJson(await core!.CallAsync(request, cancellation));
                cancellation.ThrowIfCancellationRequested();
                foreach (var section in more.Sections) AddSection(section, UseTrackRows);
                RefreshPageItems();
                emptyPages = more.Items.Count == 0 ? emptyPages + 1 : 0;
                if (emptyPages >= 3 || more.Continuation is not string next || !registeredCursors.Add(next)) return false;
                cursor = next;
                return true;
            }
            finally { indicator.IsActive = false; }
        });
    }

    private void RefreshPageItems()
    {
        if (currentPage is null) return;
        currentPage = new MusicPage
        {
            Title = currentPage.Title, PlaylistId = currentPage.PlaylistId, Actions = currentPage.Actions,
            Sections = displayedSections.ToArray(), Items = displayedSections.SelectMany(section => section.Items).ToArray(),
            Continuation = currentPage.Continuation
        };
        if (pageCountLabel is not null)
            pageCountLabel.Text = L.Get(currentView == "search" ? "SearchResultCount" : "ItemCount", currentPage.Items.Count);
    }
}
