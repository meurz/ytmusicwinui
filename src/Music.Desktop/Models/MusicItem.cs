using System.Globalization;
using System.Text.Json;

namespace Music.Desktop.Models;

public sealed class ItemActions
{
    public string? Rating { get; init; }
    public bool? InLibrary { get; init; }
    public bool? Subscribed { get; init; }
    public bool? CanEdit { get; init; }
    public string? AddLibraryToken { get; init; }
    public string? RemoveLibraryToken { get; init; }

    public static ItemActions FromJson(JsonElement value)
    {
        string? rating = JsonFields.String(value, "rating");
        return new()
        {
            Rating = rating is "like" or "dislike" or "indifferent" ? rating : null,
            InLibrary = JsonFields.Boolean(value, "in_library"),
            Subscribed = JsonFields.Boolean(value, "subscribed"),
            CanEdit = JsonFields.Boolean(value, "can_edit"),
            AddLibraryToken = JsonFields.String(value, "add_library_token"),
            RemoveLibraryToken = JsonFields.String(value, "remove_library_token")
        };
    }
}

public sealed class MusicItem
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Kind { get; init; } = "unknown";
    public string? VideoId { get; init; }
    public string? BrowseId { get; init; }
    public string? PlaylistId { get; init; }
    public string? SetVideoId { get; init; }
    public string? ImageUrl { get; init; }
    public string DurationText { get; init; } = "";
    public long? DurationSeconds { get; init; }
    public bool? Available { get; init; }
    public string AlbumTitle { get; init; } = "";
    public ItemActions Actions { get; init; } = new();

    public static MusicItem FromJson(JsonElement value)
    {
        long? duration = JsonFields.NonnegativeLong(value, "duration_seconds");
        var artists = JsonFields.Array(value, "artists")
            .Select(artist => JsonFields.String(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name));
        string? image = JsonFields.Array(value, "thumbnails")
            .OrderByDescending(thumbnail => JsonFields.NonnegativeLong(thumbnail, "width") ?? 0)
            .Select(thumbnail => SafeImageUrl(JsonFields.String(thumbnail, "url")))
            .FirstOrDefault(url => url is not null);
        return new()
        {
            Title = JsonFields.String(value, "title") ?? "",
            Subtitle = string.Join(" · ", artists),
            Kind = JsonFields.String(value, "kind") ?? "unknown",
            VideoId = JsonFields.String(value, "video_id"),
            BrowseId = JsonFields.String(value, "browse_id"),
            PlaylistId = JsonFields.String(value, "playlist_id"),
            SetVideoId = JsonFields.String(value, "set_video_id"),
            ImageUrl = image,
            DurationSeconds = duration,
            DurationText = FormatDuration(duration),
            Available = JsonFields.Boolean(value, "available"),
            AlbumTitle = JsonFields.String(JsonFields.Property(value, "album"), "name") ?? "",
            Actions = ItemActions.FromJson(JsonFields.Property(value, "actions"))
        };
    }

    public static string? SafeImageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host)
            ? uri.AbsoluteUri : null;

    private static string FormatDuration(long? seconds) => seconds switch
    {
        null => "",
        >= 3600 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}")
    };
}

internal static class JsonFields
{
    internal static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    internal static string? String(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
    internal static bool? Boolean(JsonElement value, string name) => Property(value, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
    internal static long? NonnegativeLong(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } field && field.TryGetInt64(out long number) && number >= 0 ? number : null;
    internal static IEnumerable<JsonElement> Array(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Array } field ? field.EnumerateArray() : [];
}
