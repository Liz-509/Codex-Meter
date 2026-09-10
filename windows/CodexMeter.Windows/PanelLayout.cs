namespace CodexMeter.Windows;

internal readonly record struct PanelFrame(double Left, double Top, double Width, double Height);

internal static class PanelLayout
{
    internal const double CompactSize = 66;

    public static bool IsDragHandle(double x, double y)
    {
        return double.IsFinite(x) &&
               double.IsFinite(y) &&
               x >= 0 && x < CompactSize &&
               y >= 0 && y < CompactSize;
    }

    public static PanelFrame ResizeKeepingTopLeft(
        double left,
        double top,
        double requestedWidth,
        double requestedHeight)
    {
        return new PanelFrame(
            left,
            top,
            Math.Clamp(requestedWidth, CompactSize, 360),
            Math.Clamp(requestedHeight, CompactSize, 560));
    }
}
