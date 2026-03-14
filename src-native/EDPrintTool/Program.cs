namespace EDPrintTool;

static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // Single instance check
        _mutex = new Mutex(true, @"Global\EDPrintTool_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "EDPrintTool is already running.\nCheck the system tray.",
                "EDPrintTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.SetDefaultFont(new Font("Segoe UI", 9));

        // Initialize core services
        var store = new PrinterStore();

        // Resolve public/ directory (next to exe, or dev path)
        var exeDir = AppContext.BaseDirectory;
        var publicDir = Path.Combine(exeDir, "public");
        if (!Directory.Exists(publicDir))
        {
            // Dev fallback: look relative to project
            var devPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "..", "public"));
            if (Directory.Exists(devPath))
                publicDir = devPath;
        }

        var server = new HttpServer(store, publicDir);

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start the print server:\n\n{ex.Message}\n\nPort 8189 may already be in use.",
                "EDPrintTool — Server Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Start relay client if configured (runs alongside local server)
        var relay = RelayClient.TryStart(store);
        if (relay != null)
        {
            relay.OnActivity += (msg, isError) => Console.WriteLine(msg);
        }

        var mainForm = new MainForm(store, server);
        using var tray = new TrayIcon(mainForm);

        // Check for updates in background after UI loads
        mainForm.Shown += async (_, _) =>
        {
            await Task.Delay(2000); // Let the app settle
            await UpdateChecker.CheckForUpdatesAsync();
        };

        Application.Run(mainForm);

        relay?.Stop();
        server.Stop();
        _mutex.ReleaseMutex();
    }
}
