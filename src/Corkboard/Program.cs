using System.Security.Cryptography;
using System.Text;

namespace Corkboard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        string folder = Environment.GetEnvironmentVariable("CORKBOARD_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Corkboard");
        string instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant())));
        using var mutex = new Mutex(true, $"Local\\Corkboard-{instance}", out bool first);
        if (!first)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting($"Local\\Corkboard-Show-{instance}");
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\Corkboard-Show-{instance}");
        var store = new NoteStore(folder);
        BoardData data;
        string? warning;
        try
        {
            (data, warning) = store.Load();
        }
        catch (Exception error) when (NoteStore.IsStorageError(error))
        {
            MessageBox.Show($"Corkboard couldn't open your saved notes. Your files are still in {folder}.\n\n{error.Message}",
                "Corkboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var application = new CorkboardApp(store, data, warning);
        RegisteredWaitHandle waiter = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) =>
        {
            try { application.ReopenFromOtherInstance(); }
            catch (InvalidOperationException) { }
        }, null, Timeout.Infinite, false);
        try { Application.Run(application); }
        finally { waiter.Unregister(null); }
    }
}
