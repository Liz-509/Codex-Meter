using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class CodexExecutableLocatorTests
{
    [TestMethod]
    public void Find_PrefersExplicitOverride()
    {
        const string overridePath = @"C:\Tools\codex.exe";
        var locator = new CodexExecutableLocator(path => path == overridePath);

        var result = locator.Find(
            overridePath,
            @"C:\Other",
            @"C:\Users\test\AppData\Roaming",
            @"C:\Users\test\AppData\Local",
            @"C:\Program Files");

        Assert.IsNotNull(result);
        Assert.AreEqual(overridePath, result.ExecutablePath);
    }

    [TestMethod]
    public void Find_FallsBackToNpmGlobalCommand()
    {
        var npmCommand = Path.Combine(@"C:\Users\test\AppData\Roaming", "npm", "codex.cmd");
        var locator = new CodexExecutableLocator(path => path == npmCommand);

        var result = locator.Find(
            null,
            null,
            @"C:\Users\test\AppData\Roaming",
            null,
            null);

        Assert.IsNotNull(result);
        Assert.AreEqual(npmCommand, result.ExecutablePath);
    }
}
