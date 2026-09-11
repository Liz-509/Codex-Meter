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

    public static PanelFrame ResizeWithinWorkArea(
        double left,
        double top,
        double requestedWidth,
        double requestedHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom)
    {
        var size = ResizeKeepingTopLeft(left, top, requestedWidth, requestedHeight);
        var fittedLeft = Math.Clamp(
            size.Left,
            workAreaLeft,
            Math.Max(workAreaLeft, workAreaRight - size.Width));
        var fittedTop = Math.Clamp(
            size.Top,
            workAreaTop,
            Math.Max(workAreaTop, workAreaBottom - size.Height));

        return size with { Left = fittedLeft, Top = fittedTop };
    }

}
