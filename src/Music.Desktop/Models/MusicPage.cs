using System.Text.Json;

namespace Music.Desktop.Models;

public sealed class MusicSection
{
    public string Title { get; init; } = "";
    public IReadOnlyList<MusicItem> Items { get; init; } = [];
    public string? Continuation { get; init; }

    public static MusicSection FromJson(JsonElement value) => new()
    {
        Title = JsonFields.String(value, "title") ?? "",
        Items = JsonFields.Array(value, "items").Select(MusicItem.FromJson).ToArray(),
        Continuation = JsonFields.String(value, "continuation")
    };
}

public sealed class MusicPage
{
    public string Title { get; init; } = "";
    public IReadOnlyList<MusicSection> Sections { get; init; } = [];
    public IReadOnlyList<MusicItem> Items { get; init; } = [];
    public string? PlaylistId { get; init; }
    public ItemActions Actions { get; init; } = new();
    // A feed cursor must not be replaced by a carousel cursor.
    public string? Continuation { get; init; }

    public static MusicPage FromJson(JsonElement value)
    {
        // The persistent client returns data directly. Also accept saved protocol envelopes.
        if (JsonFields.Property(value, "data").ValueKind == JsonValueKind.Object)
            value = value.GetProperty("data");
        JsonElement content = JsonFields.Property(value, "page");
        if (content.ValueKind != JsonValueKind.Object) content = value;
        MusicSection[] sections = JsonFields.Array(content, "sections").Select(MusicSection.FromJson).ToArray();
        if (sections.Length == 0 && JsonFields.Property(content, "items").ValueKind == JsonValueKind.Array)
            sections = [MusicSection.FromJson(content)];
        return new()
        {
            Title = JsonFields.String(content, "title") ?? "",
            Sections = sections,
            Items = sections.SelectMany(section => section.Items).ToArray(),
            PlaylistId = JsonFields.String(content, "playlist_id"),
            Actions = ItemActions.FromJson(JsonFields.Property(content, "actions")),
            Continuation = JsonFields.String(value, "continuation")
        };
    }
}
