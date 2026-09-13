using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CodexMeter.Windows.Services;

internal static class TrayIconRenderer
{
    public static Icon Render(int percent)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        var value = Math.Clamp(percent, 0, 100);
        var color = value < 10 ? Color.FromArgb(239, 68, 68) : value < 20 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(111, 99, 230);
        using var background = new SolidBrush(Color.FromArgb(245, 248, 250));
        using var outline = new Pen(color, 2.2f);
        graphics.FillEllipse(background, 1.5f, 1.5f, 29, 29);
        graphics.DrawEllipse(outline, 2.5f, 2.5f, 27, 27);
        var text = value.ToString();
        using var font = new Font("Segoe UI", text.Length >= 3 ? 10f : 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(31, 41, 55));
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, brush, (32 - size.Width) / 2, (32 - size.Height) / 2 - .5f);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
