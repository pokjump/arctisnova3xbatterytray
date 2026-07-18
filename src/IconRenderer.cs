using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ArctisBatteryTray;

// Draws the 32x32 tray icon showing the battery percent and releases the previous GDI handle.
// Color thresholds: >=50% green, 20-49% orange, <20% red, offline gray.
internal sealed class IconRenderer : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private const int Size = 32;

    private IntPtr _lastHandle = IntPtr.Zero;
    private bool _disposed;

    // Builds an icon for the given status. Returns an Icon ready to assign to a NotifyIcon.
    // Always releases the previous icon's handle (DestroyIcon), otherwise it leaks a GDI object.
    public Icon Build(HeadsetStatus status)
    {
        using var bmp = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            switch (status.State)
            {
                case ChargingState.DongleNotFound:
                case ChargingState.HeadsetOffline:
                    DrawGlyph(g, "–", Color.FromArgb(150, 150, 150));
                    break;

                case ChargingState.Charging:
                    DrawText(g, status.BatteryPercent, ColorFor(status.BatteryPercent));
                    DrawChargingBadge(g);
                    break;

                case ChargingState.Discharging:
                case ChargingState.TrendPending:
                    DrawText(g, status.BatteryPercent, ColorFor(status.BatteryPercent));
                    break;
            }
        }

        var handle = bmp.GetHicon();
        var icon = Icon.FromHandle(handle);

        // Release the previous handle only after the new one exists (NotifyIcon still uses it until swapped).
        if (_lastHandle != IntPtr.Zero)
            DestroyIcon(_lastHandle);
        _lastHandle = handle;

        return icon;
    }

    private static Color ColorFor(int? percent)
    {
        if (percent is not int p) return Color.FromArgb(150, 150, 150);
        if (p >= 50) return Color.FromArgb(90, 220, 120);   // green
        if (p >= 20) return Color.FromArgb(255, 170, 60);    // orange
        return Color.FromArgb(240, 80, 80);                  // red
    }

    private static void DrawText(Graphics g, int? percent, Color color)
    {
        // "100" would not fit at this size -- shown as "FL" (full) instead.
        string text = percent switch
        {
            null => "?",
            100 => "FL",
            _ => percent.Value.ToString(),
        };
        DrawGlyph(g, text, color);
    }

    private static void DrawGlyph(Graphics g, string text, Color color)
    {
        // Scale the font to the character count so it fills the 32x32 canvas.
        float fontSize = text.Length >= 3 ? 15f : 20f;

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        var rect = new RectangleF(0, -1, Size, Size);

        // Subtle shadow for legibility on both light and dark taskbars.
        using var shadow = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        var shadowRect = new RectangleF(1, 0, Size, Size);
        g.DrawString(text, font, shadow, shadowRect, fmt);
        g.DrawString(text, font, brush, rect, fmt);
    }

    private static void DrawChargingBadge(Graphics g)
    {
        // Small yellow lightning bolt in the bottom-right corner.
        var pts = new[]
        {
            new PointF(22, 17),
            new PointF(27, 17),
            new PointF(24, 23),
            new PointF(29, 23),
            new PointF(21, 32),
            new PointF(23, 25),
            new PointF(19, 25),
        };
        using var fill = new SolidBrush(Color.FromArgb(255, 225, 60));
        using var pen = new Pen(Color.FromArgb(180, 0, 0, 0), 1f);
        g.FillPolygon(fill, pts);
        g.DrawPolygon(pen, pts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lastHandle != IntPtr.Zero)
        {
            DestroyIcon(_lastHandle);
            _lastHandle = IntPtr.Zero;
        }
    }
}
