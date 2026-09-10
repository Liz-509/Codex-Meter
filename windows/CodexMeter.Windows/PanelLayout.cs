namespace CodexMeter.Windows;

internal readonly record struct PanelFrame(double Left, double Top, double Width, double Height);

internal static class PanelLayout
{
    public static PanelFrame ResizeKeepingTopLeft(
        double left,
        double top,
        double requestedWidth,
        double requestedHeight)
    {
        return new PanelFrame(
            left,
            top,
            Math.Clamp(requestedWidth, 66, 360),
            Math.Clamp(requestedHeight, 66, 560));
    }
}
