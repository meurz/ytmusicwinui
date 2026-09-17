using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YouTubeMusic.Interop;

namespace Music.Desktop.Services;

/// <summary>Owns the bundled proof provider's private, serialized JSON pipe.</summary>
/// <remarks>No account cookies, media URLs or tokens are written to disk or diagnostics.</remarks>
internal sealed class PlaybackProofProvider : IAsyncDisposable
{
    private Process? worker;
    private Task? errorDrain;

    internal sealed record Proof(string Token, long ExpiresAt);

    public async Task<Proof> GenerateAsync(string videoId, bool forceRefresh, CancellationToken cancellation)
    {
        if (videoId.Length != 11 || videoId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Invalid video ID.", nameof(videoId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // A rejected proof must also renew the upstream integrity/minter cache.
            if (forceRefresh) await StopWorkerAsync();
            if (worker is null || worker.HasExited)
            {
                await StopWorkerAsync();
                string folder = Path.Combine(AppContext.BaseDirectory, "po-provider");
                string executable = Path.Combine(folder, "node.exe");
                string script = Path.Combine(folder, "worker.cjs");
                if (!File.Exists(executable) || !File.Exists(script))
                    throw new MusicCoreException("attestation_runtime_missing", "The playback verification component is missing.");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                start.ArgumentList.Add(script);
                start.Environment.Remove("NODE_OPTIONS");
                start.Environment.Remove("NODE_PATH");
                worker = Process.Start(start) ?? throw Failure();
                errorDrain = DrainErrorsAsync(worker);
            }
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { video_id = videoId }).AsMemory(), deadline.Token);
            await worker.StandardInput.FlushAsync(deadline.Token);
            string response = await ReadResponseAsync(worker, deadline.Token);
            using var json = JsonDocument.Parse(response);
            var value = json.RootElement;
            if (!value.TryGetProperty("gvs_token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("expires_at", out var expiryValue) || !expiryValue.TryGetInt64(out long expires))
                throw Failure();
            string token = tokenValue.GetString()!;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (token.Length is < 32 or > 8192 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                || expires <= now + 30 || expires > now + 3600)
                throw Failure();
            cancellation.ThrowIfCancellationRequested();
            return new Proof(token, expires);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            await StopWorkerAsync();
            throw new MusicCoreException("timeout", "Playback verification timed out.", retryable: true);
        }
        catch (OperationCanceledException) { await StopWorkerAsync(); throw; }
        catch (MusicCoreException) { await StopWorkerAsync(); throw; }
        catch (Exception) { await StopWorkerAsync(); throw Failure(); }
    }

    private static MusicCoreException Failure() => new("attestation_failed", "Playback verification did not complete.", retryable: true);

    private static async Task<string> ReadResponseAsync(Process process, CancellationToken cancellation)
    {
        var result = new StringBuilder();
        char[] character = new char[1];
        while (result.Length <= 16384)
        {
            if (await process.StandardOutput.ReadAsync(character.AsMemory(), cancellation) == 0) throw Failure();
            if (character[0] == '\n') return result.ToString();
            if (character[0] != '\r') result.Append(character[0]);
        }
        throw Failure();
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        // Provider stderr is untrusted and may contain sensitive request details. Discard it.
        char[] buffer = new char[1024];
        int total = 0;
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) != 0)
            {
                total += read;
                if (total > 65536) { if (!process.HasExited) process.Kill(entireProcessTree: true); break; }
            }
        }
        catch (Exception) { }
    }

    private async Task StopWorkerAsync()
    {
        var previous = worker;
        worker = null;
        if (previous is null) return;
        try
        {
            if (!previous.HasExited) previous.Kill(entireProcessTree: true);
            await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { }
        finally { previous.Dispose(); }
        if (errorDrain is not null)
        {
            try { await errorDrain.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            errorDrain = null;
        }
    }

    public ValueTask DisposeAsync() => new(StopWorkerAsync());
}
