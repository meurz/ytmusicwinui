using System.Text.Json;
using YouTubeMusic.Interop;

namespace Music.Desktop.Services;

// Narrow test forwarding adapter: real upstream native calls, anonymous configuration only.
// It intentionally cannot restore/import/persist an account or access the application's store.
public sealed class CoreService(MusicCoreClient client, bool forceOpus) : IAsyncDisposable
{
    public Task<JsonElement> CallAsync(object request, CancellationToken cancellation = default,
        IProgress<MusicProgress>? progress = null)
    {
        string json = JsonSerializer.Serialize(request);
        using var parsed = JsonDocument.Parse(json);
        // Exercise the public fallback path without depending on a broken Windows AAC codec.
        if (forceOpus && parsed.RootElement.GetProperty("op").GetString() == "dash_manifest")
            throw new NotSupportedException("AAC disabled by the explicit live Opus test.");
        return client.CallAsync(json, cancellation, progress);
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
