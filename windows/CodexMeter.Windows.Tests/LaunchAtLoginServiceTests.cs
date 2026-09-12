using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class LaunchAtLoginServiceTests
{
    [TestMethod]
    public void BuildCommand_QuotesExecutablePathAndAddsStartupFlag()
    {
        var command = LaunchAtLoginService.BuildCommand(@"C:\Program Files\Codex Meter\Codex Meter.exe");

        Assert.AreEqual("\"C:\\Program Files\\Codex Meter\\Codex Meter.exe\" --startup", command);
    }
}
