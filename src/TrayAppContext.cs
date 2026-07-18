using Microsoft.Win32;

namespace ArctisBatteryTray;

/// <summary>
/// Owns the NotifyIcon, poll timer, and context menu. No main window.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    // A single HID exchange costs ~1 ms, so a 2 s poll interval is negligible overhead
    // and keeps the tray reading close to live, similar to SteelSeries GG.
    private const int PollIntervalMs = 2_000;

    private readonly HeadsetService _service = new();
    private readonly IconRenderer _renderer = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _deviceChangeDebounce;
    private readonly DeviceChangeWindow _deviceWindow;

    private ToolStripMenuItem _infoItem = null!;
    private ToolStripMenuItem _autoStartItem = null!;

    private HeadsetStatus _lastStatus = HeadsetStatus.DongleNotFound;

    public TrayAppContext()
    {
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Arctis Battery -- starting...",
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.MouseClick += OnIconClick;

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += (_, _) => PollAndUpdate("timer");

        _deviceChangeDebounce = new System.Windows.Forms.Timer { Interval = 1500 };
        _deviceChangeDebounce.Tick += (_, _) =>
        {
            _deviceChangeDebounce.Stop();
            PollAndUpdate("device-change");
        };

        _deviceWindow = new DeviceChangeWindow();
        _deviceWindow.DeviceChanged += OnDeviceChanged;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (AutoStart.IsFirstRun())
        {
            AutoStart.Enable();
            _notifyIcon.BalloonTipTitle = "Arctis Battery Tray";
            _notifyIcon.BalloonTipText = "The app will start with Windows. You can turn this off from the menu (right-click).";
            _notifyIcon.ShowBalloonTip(5000);
        }

        _timer.Start();
        PollAndUpdate("start");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _infoItem = new ToolStripMenuItem("Reading battery...") { Enabled = false };

        _autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
        };
        _autoStartItem.Click += OnToggleAutoStart;

        var refreshItem = new ToolStripMenuItem("Refresh now");
        refreshItem.Click += (_, _) => PollAndUpdate("manual");

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        // Refresh the checkbox state from the registry every time the menu opens.
        menu.Opening += (_, _) => _autoStartItem.Checked = AutoStart.IsEnabled();

        menu.Items.Add(_infoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        if (_autoStartItem.Checked)
            AutoStart.Enable();
        else
            AutoStart.Disable();
    }

    private void OnDeviceChanged()
    {
        // Debounce -- WM_DEVICECHANGE can fire several times in a row.
        _deviceChangeDebounce.Stop();
        _deviceChangeDebounce.Start();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Logger.Debug("PowerModeChanged: Resume -> immediate poll.");
            PollAndUpdate("resume");
        }
    }

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _notifyIcon.BalloonTipTitle = "Arctis Nova 3X";
        _notifyIcon.BalloonTipText = BuildTooltip(_lastStatus);
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void PollAndUpdate(string reason)
    {
        HeadsetStatus status;
        try
        {
            status = _service.Poll();
        }
        catch (Exception ex)
        {
            Logger.Error($"PollAndUpdate({reason})", ex);
            status = HeadsetStatus.Offline;
        }

        _lastStatus = status;
        Logger.Debug($"Update({reason}): state={status.State} battery={status.BatteryPercent}");

        try
        {
            _notifyIcon.Icon = _renderer.Build(status);
            _notifyIcon.Text = Truncate(BuildTooltip(status), 63);
            _infoItem.Text = BuildMenuInfo(status);
        }
        catch (Exception ex)
        {
            Logger.Error("PollAndUpdate: icon update", ex);
        }
    }

    private static string BuildTooltip(HeadsetStatus s) => s.State switch
    {
        ChargingState.DongleNotFound => "USB dongle not found",
        ChargingState.HeadsetOffline => "Headset is off",
        ChargingState.Charging => $"Arctis Nova 3X -- charging: {s.BatteryPercent}%",
        ChargingState.Discharging => $"Arctis Nova 3X -- {s.BatteryPercent}% (discharging)",
        ChargingState.TrendPending => $"Arctis Nova 3X -- {s.BatteryPercent}%",
        _ => "Arctis Battery",
    };

    private static string BuildMenuInfo(HeadsetStatus s) => s.State switch
    {
        ChargingState.DongleNotFound => "No USB dongle found",
        ChargingState.HeadsetOffline => "Headset is off",
        ChargingState.Charging => $"Battery: {s.BatteryPercent}% (charging)",
        ChargingState.Discharging => $"Battery: {s.BatteryPercent}% (discharging)",
        ChargingState.TrendPending => $"Battery: {s.BatteryPercent}%",
        _ => "Reading battery...",
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private void ExitApp()
    {
        Logger.Info("Shutting down.");
        // Icon must disappear BEFORE Application.Exit(), otherwise a dead icon lingers in the tray.
        _notifyIcon.Visible = false;
        Dispose(true);
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            _timer.Dispose();
            _deviceChangeDebounce.Dispose();
            _deviceWindow.Dispose();
            _notifyIcon.Dispose();
            _renderer.Dispose();
            _service.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Hidden window that captures WM_DEVICECHANGE (USB plug/unplug).
    /// Top-level windows receive the DBT_DEVNODES_CHANGED broadcast without registering for it.
    /// </summary>
    private sealed class DeviceChangeWindow : NativeWindow, IDisposable
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        public event Action? DeviceChanged;

        public DeviceChangeWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED)
                DeviceChanged?.Invoke();

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
