using Microsoft.Win32;

namespace ArctisBatteryTray;

// Autostart via a HKCU\...\Run entry (no admin rights required).
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ArctisBatteryTray";

    private static string ExePath => $"\"{Application.ExecutablePath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            Logger.Warn($"AutoStart.IsEnabled: {ex.Message}");
            return false;
        }
    }

    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            key!.SetValue(ValueName, ExePath, RegistryValueKind.String);
            Logger.Info($"AutoStart enabled: {ExePath}");
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart.Enable", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Info("AutoStart disabled.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AutoStart.Disable", ex);
        }
    }

    // Returns true on first run (no marker yet in the app's registry key).
    public static bool IsFirstRun()
    {
        const string appKeyPath = @"Software\ArctisBatteryTray";
        const string marker = "Initialized";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(appKeyPath);
            var existing = key!.GetValue(marker);
            if (existing is not null) return false;
            key.SetValue(marker, "1", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"AutoStart.IsFirstRun: {ex.Message}");
            return false;
        }
    }
}
