using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Music.Desktop.Localization;
namespace Music.Desktop;
internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "--verify-localization")
        {
            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                L.VerifyAllLanguages();
                Environment.ExitCode = 0;
            }
            catch (Exception error)
            {
                DiagnosticLog.Write("localization_verification_failed", error);
                Environment.ExitCode = 1;
            }
            return;
        }
        using var mutex = new Mutex(true, @"Local\ytmusicwinui.Instance", out bool first);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ytmusicwinui.Activate");
        if (!first) { activation.Set(); return; }
        RegisteredWaitHandle? wait = null;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(args =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                var app = new App();
                wait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => dispatcher.TryEnqueue(app.ActivateWindow), null, Timeout.Infinite, false);
            });
        }
        catch (Exception error) { DiagnosticLog.Write("startup_failure", error); throw; }
        finally { wait?.Unregister(null); mutex.ReleaseMutex(); }
    }
}
internal static class DiagnosticLog
{
    public static void Write(string category, Exception error)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ytmusicwinui");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 262144) File.Move(path, path + ".previous", true);
            // Exception text, request bodies, URLs and session values never enter diagnostics.
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {category} {error.GetType().Name} 0x{error.HResult:X8}\n");
        }
        catch { }
    }
}
