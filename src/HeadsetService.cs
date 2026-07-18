using System.Text;
using HidSharp;

namespace ArctisBatteryTray;

internal enum ChargingState
{
    // Battery percent dropped since the previous reading (direct byte read, or trend fallback).
    Discharging,
    // Battery percent rose since the previous reading (direct byte read, or trend fallback).
    Charging,
    // Battery available but direction not yet known (first reading, or byte value unrecognized).
    TrendPending,
    // Dongle present but headset is off / out of range (HID connection byte = 0x02, or read timeout).
    HeadsetOffline,
    // No SteelSeries USB dongle found.
    DongleNotFound,
}

internal readonly record struct HeadsetStatus(int? BatteryPercent, ChargingState State)
{
    public static HeadsetStatus DongleNotFound => new(null, ChargingState.DongleNotFound);
    public static HeadsetStatus Offline => new(null, ChargingState.HeadsetOffline);
}

// All HID logic: SteelSeries device enumeration, active-interface selection,
// battery request/response, and caching of the last working device path.
internal sealed class HeadsetService : IDisposable
{
    private const int SteelSeriesVid = 0x1038;
    private const int PidNova3XWireless = 0x226d;

    // Known PIDs for the Nova 3 Wireless family (2.4 GHz dongle). Nova 3P is the twin model.
    private static readonly int[] KnownNova3Pids = { 0x226d, 0x2269 };

    // Arctis Nova family protocol: Output Report [reportId=0x00, 0xb0].
    private const byte BatteryReportByte = 0xb0;

    // Verified empirically on a Nova 3X (PID 0x226d, interface mi_03). HeadsetControl
    // (steelseries_arctis_nova_3p_wireless.hpp -- getBattery()) only reads 4 response bytes,
    // so it only sees the offline marker and never decodes charge state.
    // HidSharp prepends a report-ID byte (0x00) at index 0, so in our 65-byte buffer:
    //   buf = [0x00, 0xb0, connection, _, battery%, charging, 0x64, 0x64, 0x00, ...]
    // Relative to the 0xb0 marker: connection = marker+1, battery = marker+3, charging = marker+4.
    private const int ConnectionOffsetFromMarker = 1;
    private const int BatteryOffsetFromMarker = 3;
    private const int ChargeOffsetFromMarker = 4;

    // Connection byte (buf[marker+1]) -- the only verified meaning is offline (HeadsetControl: HEADSET_OFFLINE).
    private const byte StatusOffline = 0x02;

    // Charge byte (buf[marker+4]) -- outside the 4-byte window HeadsetControl reads, but real and
    // confirmed empirically: 0x01 while charging over cable, 0x03 while running wirelessly on
    // battery (discharging). Same values the original hardware plan assumed for the Nova 7 family,
    // just one byte further along than expected.
    private const byte ChargeCharging = 0x01;
    private const byte ChargeDischarging = 0x03;

    private const int ReadTimeoutMs = 200;
    private const int WriteTimeoutMs = 200;
    private const int MaxConsecutiveErrors = 3;

    private HidStream? _stream;
    private string? _activePath;
    private int _consecutiveErrors;
    private bool _disposed;

    // Trend-based charge direction fallback, used only when the direct charge byte is missing/unknown.
    private int? _lastPercent;
    private ChargingState _lastDirection = ChargingState.TrendPending;

    // One poll cycle. Never throws -- always returns a usable status.
    public HeadsetStatus Poll()
    {
        if (_disposed) return HeadsetStatus.DongleNotFound;

        try
        {
            if (_stream is null && !TryAcquireDevice())
                return HeadsetStatus.DongleNotFound;

            var status = QueryOnce(_stream!);
            _consecutiveErrors = 0;
            return ApplyTrend(status);
        }
        catch (Exception ex)
        {
            // IO/timeout -- dongle unplugged or a transient error. Reset the cache, full re-enumeration next poll.
            Logger.Debug($"Poll: exception {ex.GetType().Name}: {ex.Message}. Resetting handle.");
            ResetStream();
            _consecutiveErrors++;

            if (_consecutiveErrors >= MaxConsecutiveErrors)
            {
                Logger.Warn($"Poll: {_consecutiveErrors} consecutive errors -- treating as offline.");
                _lastPercent = null;
                return HeadsetStatus.Offline;
            }
            // Below the error threshold, report dongle-not-found (next poll retries).
            return HeadsetStatus.DongleNotFound;
        }
    }

    // The charge byte (buf[marker+4]) is read directly in Parse() and is authoritative --
    // this method is only a trend-based fallback (rising/falling percent) for when
    // Parse() returns ChargingState.TrendPending (byte missing/unrecognized).
    private HeadsetStatus ApplyTrend(HeadsetStatus status)
    {
        if (status.BatteryPercent is not int percent)
        {
            _lastPercent = null;
            return status;
        }

        if (status.State != ChargingState.TrendPending)
        {
            // Direct read is conclusive -- just update history.
            _lastPercent = percent;
            _lastDirection = status.State;
            return status;
        }

        ChargingState direction;
        if (_lastPercent is int prev)
        {
            direction = percent > prev ? ChargingState.Charging
                : percent < prev ? ChargingState.Discharging
                : _lastDirection;
        }
        else
        {
            direction = ChargingState.TrendPending;
        }

        _lastPercent = percent;
        _lastDirection = direction;
        return status with { State = direction };
    }

    // Sends the request on an already-open stream and parses the response.
    // Throws on IO/timeout (handled in Poll()).
    private HeadsetStatus QueryOnce(HidStream stream)
    {
        WriteBatteryRequest(stream);

        var buf = new byte[Math.Max(stream.Device.GetMaxInputReportLength(), 8)];
        var deadline = Environment.TickCount + ReadTimeoutMs;

        // The dongle can be chatty with other reports -- keep reading until we see 0xb0 or time out.
        while (Environment.TickCount < deadline)
        {
            stream.ReadTimeout = Math.Max(1, deadline - Environment.TickCount);
            int read;
            try
            {
                read = stream.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                break;
            }

            if (read <= 0) continue;

            int marker = FindMarker(buf, read);
            if (marker < 0) continue; // some other report -- keep reading

            return Parse(buf, read, marker);
        }

        // No battery report within the time window -> headset offline.
        Logger.Debug("QueryOnce: no 0xb0 report within the time window -> offline.");
        return HeadsetStatus.Offline;
    }

    // Locates the 0xb0 marker byte. On Windows, HidSharp prepends a report-ID byte, so the marker
    // is usually at index 1; index 0 is also accepted for robustness. Returns the marker index or -1.
    private static int FindMarker(byte[] buf, int read)
    {
        if (read > 0 && buf[0] == BatteryReportByte) return 0;
        if (read > 1 && buf[1] == BatteryReportByte) return 1;
        return -1;
    }

    // Parses the report [0xb0, connection, _, battery%, charging] relative to the marker index.
    private static HeadsetStatus Parse(byte[] buf, int read, int marker)
    {
        int connIdx = marker + ConnectionOffsetFromMarker;
        int batteryIdx = marker + BatteryOffsetFromMarker;
        int chargeIdx = marker + ChargeOffsetFromMarker;

        if (batteryIdx >= read)
        {
            Logger.Debug($"Parse: report too short ({read} B): {ToHex(buf, read)}");
            return HeadsetStatus.Offline;
        }

        byte connection = buf[connIdx];
        byte rawBattery = buf[batteryIdx];
        byte? chargeByte = chargeIdx < read ? buf[chargeIdx] : null;

        Logger.Debug($"Parse: raw={ToHex(buf, read)} connection=0x{connection:x2} battery={rawBattery} charge={(chargeByte.HasValue ? $"0x{chargeByte:x2}" : "none")}");

        if (connection == StatusOffline)
            return HeadsetStatus.Offline;

        int percent = NormalizeBattery(rawBattery);
        if (percent < 0)
        {
            Logger.Warn($"Parse: battery out of range (raw={rawBattery}). Discarding reading.");
            throw new InvalidDataException($"Battery out of range: {rawBattery}");
        }

        // Direct charge-direction read; TrendPending (trend fallback in Poll()/ApplyTrend)
        // only when the byte is missing or has an unrecognized value.
        var state = chargeByte switch
        {
            ChargeCharging => ChargingState.Charging,
            ChargeDischarging => ChargingState.Discharging,
            _ => ChargingState.TrendPending,
        };

        return new HeadsetStatus(percent, state);
    }

    // The Nova 3X reports battery as a direct 0-100 percent (confirmed by HeadsetControl:
    // map(raw,0,100,0,100)). Returns -1 when the value is clearly invalid (>100).
    private static int NormalizeBattery(byte raw)
    {
        if (raw <= 100)
            return raw;
        return -1;
    }

    private void WriteBatteryRequest(HidStream stream)
    {
        int outLen = Math.Max(stream.Device.GetMaxOutputReportLength(), 2);
        var report = new byte[outLen];
        report[0] = 0x00;             // report ID 0
        report[1] = BatteryReportByte; // 0xb0
        stream.WriteTimeout = WriteTimeoutMs;
        stream.Write(report, 0, report.Length);
    }

    // Enumerates SteelSeries devices and picks the first one that answers a 0xb0 report.
    // Caches the open stream so we don't re-enumerate on every poll.
    private bool TryAcquireDevice()
    {
        foreach (var dev in EnumerateCandidates())
        {
            HidStream? stream = null;
            try
            {
                if (!dev.TryOpen(out stream))
                {
                    Logger.Debug($"TryAcquire: could not open {dev.DevicePath}");
                    continue;
                }

                stream.ReadTimeout = ReadTimeoutMs;
                stream.WriteTimeout = WriteTimeoutMs;

                WriteBatteryRequest(stream);

                var buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 8)];
                var deadline = Environment.TickCount + ReadTimeoutMs;
                bool responded = false;
                while (Environment.TickCount < deadline)
                {
                    stream.ReadTimeout = Math.Max(1, deadline - Environment.TickCount);
                    try
                    {
                        int read = stream.Read(buf, 0, buf.Length);
                        if (read > 0 && FindMarker(buf, read) >= 0)
                        {
                            responded = true;
                            break;
                        }
                    }
                    catch (TimeoutException) { break; }
                }

                if (responded)
                {
                    _stream = stream;
                    _activePath = dev.DevicePath;
                    Logger.Info($"Active interface: PID=0x{dev.ProductID:x4} path={dev.DevicePath}");
                    return true;
                }

                stream.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Debug($"TryAcquire: {dev.DevicePath} exception {ex.GetType().Name}: {ex.Message}");
                stream?.Dispose();
            }
        }

        Logger.Debug("TryAcquire: no interface responded to 0xb0.");
        return false;
    }

    // Candidate order: exact Nova 3X PID -> known family PIDs -> all VID 0x1038 devices (probe-all).
    private static IEnumerable<HidDevice> EnumerateCandidates()
    {
        var all = DeviceList.Local.GetHidDevices(SteelSeriesVid).ToList();

        foreach (var d in all)
            Logger.Debug($"Enum: PID=0x{d.ProductID:x4} inLen={SafeLen(d, input: true)} outLen={SafeLen(d, input: false)} path={d.DevicePath}");

        var exact = all.Where(d => d.ProductID == PidNova3XWireless).ToList();
        var known = all.Where(d => KnownNova3Pids.Contains(d.ProductID) && d.ProductID != PidNova3XWireless).ToList();
        var rest = all.Where(d => !KnownNova3Pids.Contains(d.ProductID)).ToList();

        foreach (var d in exact) yield return d;
        foreach (var d in known) yield return d;
        foreach (var d in rest) yield return d;
    }

    private void ResetStream()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _activePath = null;
    }

    // Diagnostic mode (--probe): lists all VID 0x1038 devices, sends [0x00,0xb0]
    // and prints raw hex responses. The key hardware-verification step for this project.
    public static string Probe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Enumerating HID devices VID=0x{SteelSeriesVid:x4} (SteelSeries):");
        sb.AppendLine();

        var devices = DeviceList.Local.GetHidDevices(SteelSeriesVid).ToList();
        if (devices.Count == 0)
        {
            sb.AppendLine("  (no devices found -- is the dongle plugged in?)");
            return sb.ToString();
        }

        foreach (var dev in devices)
        {
            sb.AppendLine($"PID=0x{dev.ProductID:x4}  inLen={SafeLen(dev, true)}  outLen={SafeLen(dev, false)}");
            sb.AppendLine($"  path: {dev.DevicePath}");

            HidStream? stream = null;
            try
            {
                if (!dev.TryOpen(out stream))
                {
                    sb.AppendLine("  -> could not open (busy / access denied)");
                    sb.AppendLine();
                    continue;
                }

                stream.WriteTimeout = WriteTimeoutMs;
                stream.ReadTimeout = ReadTimeoutMs;

                int outLen = Math.Max(dev.GetMaxOutputReportLength(), 2);
                var report = new byte[outLen];
                report[0] = 0x00;
                report[1] = BatteryReportByte;
                stream.Write(report, 0, report.Length);

                var buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 8)];
                var deadline = Environment.TickCount + ReadTimeoutMs;
                bool any = false;
                while (Environment.TickCount < deadline)
                {
                    stream.ReadTimeout = Math.Max(1, deadline - Environment.TickCount);
                    try
                    {
                        int read = stream.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            any = true;
                            sb.AppendLine($"  <- response ({read} B): {ToHex(buf, read)}");
                            int marker = FindMarker(buf, read);
                            if (marker >= 0 && marker + BatteryOffsetFromMarker < read)
                            {
                                byte connection = buf[marker + ConnectionOffsetFromMarker];
                                byte battery = buf[marker + BatteryOffsetFromMarker];
                                int chargeIdx = marker + ChargeOffsetFromMarker;
                                string chargeInfo = chargeIdx < read ? $"0x{buf[chargeIdx]:x2}" : "none";
                                sb.AppendLine($"     *** battery report: marker@{marker} connection=0x{connection:x2} " +
                                              $"battery={battery} (normalized: {NormalizeBattery(battery)}%) charge={chargeInfo} ***");
                                break;
                            }
                        }
                    }
                    catch (TimeoutException) { break; }
                }

                if (!any)
                    sb.AppendLine("  <- no response within 200 ms");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  -> error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string SafeLen(HidDevice d, bool input)
    {
        try { return (input ? d.GetMaxInputReportLength() : d.GetMaxOutputReportLength()).ToString(); }
        catch { return "?"; }
    }

    private static string ToHex(byte[] buf, int len)
    {
        var sb = new StringBuilder(len * 3);
        for (int i = 0; i < len; i++)
            sb.Append(buf[i].ToString("x2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetStream();
    }
}
