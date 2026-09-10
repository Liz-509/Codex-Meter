using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class JsonLineBufferTests
{
    [TestMethod]
    public void Append_ReassemblesPartialLinesAndSkipsMalformedInput()
    {
        var buffer = new JsonLineBuffer();

        Assert.AreEqual(0, buffer.Append("{\"id\":1,").Count);
        var first = buffer.Append("\"result\":{}}\nnot-json\n{\"id\":");
        var second = buffer.Append("2,\"result\":{}}\r\n");

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, first[0].GetProperty("id").GetInt32());
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(2, second[0].GetProperty("id").GetInt32());
    }
}
