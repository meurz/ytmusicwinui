using System.Security.Cryptography;

namespace Music.Desktop.Services;

/// <summary>Stores only DPAPI ciphertext, scoped to the current Windows user.</summary>
public sealed class SessionStore
{
    private const int MaximumSessionBytes = 1024 * 1024;
    private static readonly byte[] Entropy = "ytmusicwinui/session/v1"u8.ToArray();
    private readonly string directory;
    private readonly string path;

    public SessionStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ytmusicwinui");
        path = Path.Combine(this.directory, "session.dpapi");
    }

    /// <summary>The caller must zero the returned plaintext after use.</summary>
    public async Task<byte[]?> ReadAsync(CancellationToken cancellation = default)
    {
        if (!File.Exists(path)) return null;
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaximumSessionBytes) throw new InvalidDataException("Saved session is invalid.");
            encrypted = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(encrypted, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length > MaximumSessionBytes) throw new InvalidDataException("Saved session is invalid.");
            cancellation.ThrowIfCancellationRequested();
            byte[] result = plaintext;
            plaintext = null;
            return result;
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SaveAsync(ReadOnlyMemory<byte> session, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (session.Length is <= 0 or > MaximumSessionBytes) throw new InvalidDataException("Session is invalid.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"session-{Guid.NewGuid():N}.tmp");
        byte[] plaintext = session.ToArray();
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellation).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            // Commit point: never observe cancellation after replacing a verified session.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public Task DeleteAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }
}
