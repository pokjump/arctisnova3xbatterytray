namespace ArctisBatteryTray;

static class Program
{
    private const string MutexName = @"Global\ArctisBatteryTray";

    [STAThread]
    static int Main(string[] args)
    {
        bool debug = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
        bool probe = args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));

        Logger.Init(debug || probe);

        // Diagnostic mode: console HID enumeration and battery-request test.
        if (probe)
            return RunProbe();

        // Single instance only.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Logger.Info("Second instance -- exiting quietly.");
            return 0;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayAppContext());
        }
        catch (Exception ex)
        {
            Logger.Error("Unhandled exception in Main", ex);
            return 1;
        }

        return 0;
    }

    // --probe mode: enumerates VID 0x1038 devices, sends [0x00,0xb0], and prints raw hex
    // responses. Attaches to the parent console if one exists.
    private static int RunProbe()
    {
        AttachConsole();

        string report = HeadsetService.Probe();
        Console.WriteLine();
        Console.WriteLine(report);
        Console.WriteLine($"(Log: {Logger.LogPath})");

        return 0;
    }

    // A WinExe has no console of its own -- attach to the parent's (e.g. PowerShell) to show --probe output.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    private static void AttachConsole()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { /* no console available -- fine */ }
    }
}
