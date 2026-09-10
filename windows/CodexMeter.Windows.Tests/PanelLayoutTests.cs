using CodexMeter.Windows;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class PanelLayoutTests
{
    [TestMethod]
    public void IsDragHandle_AcceptsIconAreaWhilePanelIsExpanded()
    {
        Assert.IsTrue(PanelLayout.IsDragHandle(32, 32));
        Assert.IsTrue(PanelLayout.IsDragHandle(0, 0));
        Assert.IsTrue(PanelLayout.IsDragHandle(65.99, 65.99));
    }

    [TestMethod]
    public void IsDragHandle_RejectsExpandedPanelContentAndInvalidCoordinates()
    {
        Assert.IsFalse(PanelLayout.IsDragHandle(66, 20));
        Assert.IsFalse(PanelLayout.IsDragHandle(20, 66));
        Assert.IsFalse(PanelLayout.IsDragHandle(-1, 20));
        Assert.IsFalse(PanelLayout.IsDragHandle(double.NaN, 20));
    }

    [TestMethod]
    public void ResizeKeepingTopLeft_DoesNotMovePanelBackIntoWorkArea()
    {
        var frame = PanelLayout.ResizeKeepingTopLeft(
            left: 1900,
            top: -12,
            requestedWidth: 360,
            requestedHeight: 443);

        Assert.AreEqual(1900, frame.Left);
        Assert.AreEqual(-12, frame.Top);
        Assert.AreEqual(360, frame.Width);
        Assert.AreEqual(443, frame.Height);
    }

    [TestMethod]
    public void ResizeKeepingTopLeft_StillClampsUnsupportedSizes()
    {
        var frame = PanelLayout.ResizeKeepingTopLeft(
            left: 20,
            top: 30,
            requestedWidth: 900,
            requestedHeight: 10);

        Assert.AreEqual(20, frame.Left);
        Assert.AreEqual(30, frame.Top);
        Assert.AreEqual(360, frame.Width);
        Assert.AreEqual(66, frame.Height);
    }
}
