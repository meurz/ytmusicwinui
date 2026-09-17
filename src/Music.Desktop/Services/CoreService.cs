using System.Security.Cryptography;
using System.Text.Json;
using YouTubeMusic.Interop;

namespace Music.Desktop.Services;

/// <summary>Coordinates the upstream interop client and atomic, verified session changes.</summary>
public sealed class CoreService : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SessionStore store;
    private readonly string language;
    private MusicCoreClient client;
    private readonly MusicCoreClient anonymousClient;
    private bool disposed;

    private CoreService(MusicCoreClient client, SessionStore store, string language)
    {
        this.client = client;
        anonymousClient = client;
        this.store = store;
        this.language = language;
    }

    public bool IsAuthenticated { get; private set; }
    public string AccountName { get; private set; } = "";
    public string CoreVersion { get; private set; } = "";
    public string SessionStatus { get; private set; } = "signed_out";

    public static async Task<CoreService> CreateAsync(CancellationToken cancellation = default, string language = "en-US")
    {
        MusicCoreClient anonymous = await MusicCoreClient.CreateAsync(DefaultConfig(language), cancellation).ConfigureAwait(false);
        var service = new CoreService(anonymous, new SessionStore(), language);
        try
        {
            JsonElement capabilities = await anonymous.CallAsync("{\"op\":\"capabilities\"}", cancellation).ConfigureAwait(false);
            if (capabilities.GetProperty("abi_version").GetInt32() != 2 || capabilities.GetProperty("protocol_version").GetString() != "2.0")
                throw new InvalidOperationException("The installed Music core is incompatible.");
            service.CoreVersion = capabilities.GetProperty("core_version").GetString() ?? "";
            await service.RestoreAsync(cancellation).ConfigureAwait(false);
            return service;
        }
        catch
        {
            await service.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(object request, CancellationToken cancellation = default,
        IProgress<MusicProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            JsonElement result = await client.CallAsync(JsonSerializer.Serialize(request), cancellation, progress).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("state", out var state)
                && state.ValueKind == JsonValueKind.String && state.GetString() is "rejected" or "signed_out")
                MarkSignedOut(state.GetString()!);
            return result;
        }
        catch (MusicCoreException error)
        {
            if (error.Code is "authentication_rejected" or "authentication_required") MarkSignedOut("rejected");
            throw Sanitize(error);
        }
        finally { gate.Release(); }
    }

    /// <summary>Imports the Cookie request-header value copied from music.youtube.com.</summary>
    public async Task ImportCookieAsync(string cookie, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Length > 65536 || cookie.Any(char.IsControl))
            throw new ArgumentException("Enter a valid Cookie request-header value.", nameof(cookie));
        cookie = cookie.Trim();
        if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = cookie[7..].Trim();
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        MusicCoreClient? candidate = null;
        try
        {
            ThrowIfDisposed();
            candidate = await MusicCoreClient.CreateAsync(JsonSerializer.Serialize(new
            {
                language, country = "TW", playback_client = "web_remix", cookie
            }), cancellation).ConfigureAwait(false);
            JsonElement account = await VerifyAsync(candidate, cancellation).ConfigureAwait(false);
            await PersistAsync(candidate, cancellation).ConfigureAwait(false);
            MusicCoreClient previous = client;
            client = candidate;
            candidate = null;
            SetAccount(account);
            if (!ReferenceEquals(previous, anonymousClient))
                await DisposeQuietlyAsync(previous).ConfigureAwait(false);
        }
        catch (MusicCoreException error) { throw Sanitize(error); }
        catch (Exception error) when (IsStorageError(error)) { throw new InvalidOperationException("The verified session could not be saved securely."); }
        finally
        {
            if (candidate is not null) await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
            gate.Release();
        }
    }

    public async Task SelectAccountAsync(JsonElement selector, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        MusicCoreClient? candidate = null;
        try
        {
            ThrowIfDisposed();
            candidate = await client.SelectAccountAsync(selector.GetRawText(), cancellation).ConfigureAwait(false);
            JsonElement account = await VerifyAsync(candidate, cancellation).ConfigureAwait(false);
            await PersistAsync(candidate, cancellation).ConfigureAwait(false);
            MusicCoreClient previous = client;
            client = candidate;
            candidate = null;
            SetAccount(account);
            if (!ReferenceEquals(previous, anonymousClient))
                await DisposeQuietlyAsync(previous).ConfigureAwait(false);
        }
        catch (MusicCoreException error) { throw Sanitize(error); }
        catch (Exception error) when (IsStorageError(error)) { throw new InvalidOperationException("The selected account could not be saved securely."); }
        finally
        {
            if (candidate is not null) await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
            gate.Release();
        }
    }

    public async Task MaintainSessionAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsAuthenticated) return;
            JsonElement refresh = await client.RefreshAndPersistAsync(session => store.SaveAsync(session, cancellation), cancellation).ConfigureAwait(false);
            SetAccount(refresh.GetProperty("account"));
        }
        catch (MusicCoreException error)
        {
            if (error.Code is "authentication_rejected" or "authentication_required") MarkSignedOut("rejected");
            throw Sanitize(error);
        }
        catch (Exception error) when (IsStorageError(error)) { throw new InvalidOperationException("The refreshed session could not be saved securely."); }
        finally { gate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // Sign-out must work offline. Reuse the already bootstrapped anonymous client.
            await store.DeleteAsync(cancellation).ConfigureAwait(false);
            MusicCoreClient previous = client;
            client = anonymousClient;
            MarkSignedOut("signed_out");
            if (!ReferenceEquals(previous, anonymousClient))
                await DisposeQuietlyAsync(previous).ConfigureAwait(false);
        }
        catch (Exception error) when (IsStorageError(error)) { throw new InvalidOperationException("The saved session could not be removed."); }
        finally { gate.Release(); }
    }

    private async Task RestoreAsync(CancellationToken cancellation)
    {
        byte[]? saved = null;
        MusicCoreClient? candidate = null;
        try
        {
            saved = await store.ReadAsync(cancellation).ConfigureAwait(false);
            if (saved is null) return;
            using JsonDocument document = JsonDocument.Parse(saved);
            var config = new Dictionary<string, object?>
            {
                ["language"] = language, ["country"] = "TW", ["playback_client"] = "web_remix"
            };
            foreach (string field in new[] { "cookie", "cookie_expirations", "auth_user", "delegated_session_id" })
                if (document.RootElement.TryGetProperty(field, out var value)) config[field] = value;
            if (!config.ContainsKey("cookie")) throw new InvalidDataException("Saved session is invalid.");
            candidate = await MusicCoreClient.CreateAsync(JsonSerializer.Serialize(config), cancellation).ConfigureAwait(false);
            JsonElement account = await VerifyAsync(candidate, cancellation).ConfigureAwait(false);
            // Keep any rotated, verified response cookies before adopting this account.
            await PersistAsync(candidate, cancellation).ConfigureAwait(false);
            MusicCoreClient previous = client;
            client = candidate;
            candidate = null;
            SetAccount(account);
            if (!ReferenceEquals(previous, anonymousClient))
                await DisposeQuietlyAsync(previous).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (MusicCoreException error)
        {
            // Never erase a rejected session implicitly; anonymous discovery remains usable.
            MarkSignedOut(error.Code is "authentication_rejected" or "authentication_required" ? "rejected" : "restore_failed");
        }
        catch (Exception error) when (IsStorageError(error) || error is JsonException or InvalidOperationException)
        {
            MarkSignedOut("restore_failed");
        }
        finally
        {
            if (saved is not null) CryptographicOperations.ZeroMemory(saved);
            if (candidate is not null) await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
        }
    }

    private static Task<JsonElement> VerifyAsync(MusicCoreClient candidate, CancellationToken cancellation) =>
        candidate.CallAsync("{\"op\":\"account\"}", cancellation);

    private async Task PersistAsync(MusicCoreClient candidate, CancellationToken cancellation)
    {
        if (!await candidate.ExportSessionToAsync(session => store.SaveAsync(session, cancellation), cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("The account did not provide a verified session.");
    }

    private void SetAccount(JsonElement account)
    {
        AccountName = account.GetProperty("name").GetString() ?? "";
        IsAuthenticated = true;
        SessionStatus = "authenticated";
    }

    private void MarkSignedOut(string status)
    {
        IsAuthenticated = false;
        AccountName = "";
        SessionStatus = status;
    }

    private static string DefaultConfig(string language) => JsonSerializer.Serialize(new { language, country = "TW", playback_client = "web_remix" });

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or CryptographicException;

    // Deliberately exclude exception details, request URLs and the native message.
    private static MusicCoreException Sanitize(MusicCoreException error) => new(error.Code, error.Code switch
    {
        "authentication_rejected" => "The account session has expired. Import a new Cookie to sign in.",
        "authentication_required" => "Sign in to use this feature.",
        "timeout" => "The Music request timed out.",
        "network" => "Could not connect to YouTube Music.",
        "po_token_required" => "This track requires additional playback verification.",
        "invalid_input" => "The Music request contains an invalid value.",
        _ => "The Music request could not be completed."
    }, error.Retryable, error.HttpStatus, error.RetryAfterSeconds);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static async Task DisposeQuietlyAsync(MusicCoreClient value)
    {
        try { await value.DisposeAsync().ConfigureAwait(false); }
        catch (MusicCoreException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            if (!ReferenceEquals(client, anonymousClient))
                await DisposeQuietlyAsync(anonymousClient).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
