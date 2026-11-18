namespace MemoryEditor;

static class Program
{
    [STAThread]
    static void Main()
    {
        // must run before any window is created (SetUnhandledExceptionMode)
        MemoryEditor.UI.CrashGuard.Install();
        ApplicationConfiguration.Initialize();

        try
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args.Contains("--scanner"))
            {
                Application.Run(new ValueScannerForm());
            }
            else
            {
                Application.Run(new MainForm());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error starting UI: {ex}");
            throw;
        }
    }
}
