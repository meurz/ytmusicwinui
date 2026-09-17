using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Music.Desktop.Models;
using Music.Desktop.Services;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
using var fixture = JsonDocument.Parse("""
{"title":"Feed", "continuation":"feed-only", "playlist_id":"P", "actions":{"can_edit":false}, "sections":[
 {"title":"Songs", "continuation":"carousel-only", "items":[
  {"title":"Track", "video_id":"abcdefghijk", "kind":"song", "set_video_id":"entry-1", "artists":[{"name":"Artist"}], "album":{"name":"Album"}, "duration_seconds":3661,"thumbnails":[{"url":"https://user:secret@evil.example/image","width":800},{"url":"file:///secret","width":700},{"url":"https://i.ytimg.com/cover","width":300}],"actions":{"rating":"like","in_library":false,"add_library_token":"opaque"}},
  {"title":"Duplicate", "video_id":"abcdefghijk", "set_video_id":"entry-2", "available":false,"actions":{"rating":"future"}}
 ]}]}
""");
var page = MusicPage.FromJson(fixture.RootElement);
Check(page.Continuation == "feed-only" && page.Sections[0].Continuation == "carousel-only", "Distinct cursor scopes");
Check(page.Items.Count == 2 && page.Items[0].SetVideoId != page.Items[1].SetVideoId, "Duplicate playlist identity");
Check(page.Items[0].DurationText == "1:01:01" && page.Items[0].Subtitle == "Artist", "Track presentation");
Check(page.Items[0].ImageUrl == "https://i.ytimg.com/cover", "Safe image scheme and credentials");
Check(page.Actions.CanEdit == false && page.Items[0].Actions.CanEdit == null, "Header actions are separate");
Check(page.Items[0].Available == null && page.Items[1].Available == false, "Unknown availability is preserved");
Check(page.Items[0].Actions.InLibrary == false && page.Items[1].Actions.InLibrary == null && page.Items[1].Actions.Rating == null, "Unknown states are not false");
using var wrapper = JsonDocument.Parse("""{"data":{"page":{"title":"Wrapped","sections":[]},"continuation":"outer"}}""");
Check(MusicPage.FromJson(wrapper.RootElement).Title == "Wrapped" && MusicPage.FromJson(wrapper.RootElement).Continuation == "outer", "Wrapped data");
var directory = Path.Combine(Path.GetTempPath(), "ytmusicwinui-test-" + Guid.NewGuid().ToString("N"));
var store = new SessionStore(directory);
var first = Encoding.UTF8.GetBytes("{\"cookie\":\"synthetic-only\"}");
var second = Encoding.UTF8.GetBytes("{\"cookie\":\"synthetic-replacement\"}");
try
{
 Check(await store.ReadAsync() == null, "Absent session");
 await store.SaveAsync(first);
 var ciphertext = await File.ReadAllBytesAsync(Path.Combine(directory, "session.dpapi"));
 Check(!Encoding.UTF8.GetString(ciphertext).Contains("synthetic"), "No plaintext file");
 var decrypted = (await store.ReadAsync())!;
 Check(first.SequenceEqual(decrypted), "CurrentUser DPAPI roundtrip");
 CryptographicOperations.ZeroMemory(decrypted);
 using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
 try { await store.SaveAsync(second, cancelled.Token); throw new Exception("Cancellation ignored"); }
 catch (OperationCanceledException) { }
 decrypted = (await store.ReadAsync())!;
 Check(first.SequenceEqual(decrypted), "Cancelled replacement preserves original");
 CryptographicOperations.ZeroMemory(decrypted);
 await store.SaveAsync(second);
 decrypted = (await store.ReadAsync())!;
 Check(second.SequenceEqual(decrypted), "Atomic replacement decrypts");
 CryptographicOperations.ZeroMemory(decrypted);
 Check(Directory.GetFiles(directory).Length == 1, "No temporary files");
 await File.WriteAllBytesAsync(Path.Combine(directory,"session.dpapi"), [1,2,3,4]);
 try { await store.ReadAsync(); throw new Exception("Corruption accepted"); } catch (CryptographicException) { }
 Check(File.Exists(Path.Combine(directory,"session.dpapi")), "Rejected ciphertext preserved");
 await store.DeleteAsync(); Check(await store.ReadAsync() == null, "Explicit deletion");
}
finally { CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(second); if (Directory.Exists(directory)) Directory.Delete(directory,true); }
Console.WriteLine($"PASS: {checks} model and Windows DPAPI contract checks.");
