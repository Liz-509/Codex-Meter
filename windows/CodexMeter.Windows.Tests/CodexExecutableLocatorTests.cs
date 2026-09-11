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

    [TestMethod]
    public void Find_UsesOfficialStandaloneInstallDirectory()
    {
        var standalone = Path.Combine(
            @"C:\Users\test\AppData\Local",
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        var locator = new CodexExecutableLocator(path => path == standalone);

        var result = locator.Find(
            null,
            null,
            null,
            @"C:\Users\test\AppData\Local",
            null);

        Assert.IsNotNull(result);
        Assert.AreEqual(standalone, result.ExecutablePath);
    }

    [TestMethod]
    public void Find_UsesRelocatedDesktopRuntime()
    {
        var desktopRuntime = Path.Combine(
            @"C:\Users\test\AppData\Local",
            "OpenAI",
            "Codex",
            "bin",
            "runtime-hash",
            "codex.exe");
        var locator = new CodexExecutableLocator(
            path => path == desktopRuntime,
            directory => directory.EndsWith(Path.Combine("OpenAI", "Codex", "bin"), StringComparison.OrdinalIgnoreCase)
                ? [desktopRuntime]
                : []);

        var result = locator.Find(
            null,
            null,
            null,
            @"C:\Users\test\AppData\Local",
            null);

        Assert.IsNotNull(result);
        Assert.AreEqual(desktopRuntime, result.ExecutablePath);
    }

    [TestMethod]
    public void Find_UsesPackagedDesktopCache()
    {
        var packagedRuntime = Path.Combine(
            @"C:\Users\test\AppData\Local",
            "Packages",
            "OpenAI.Codex_2p2nqsd0c76g0",
            "LocalCache",
            "Local",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        var locator = new CodexExecutableLocator(path => path == packagedRuntime);

        var result = locator.Find(
            null,
            null,
            null,
            @"C:\Users\test\AppData\Local",
            null);

        Assert.IsNotNull(result);
        Assert.AreEqual(packagedRuntime, result.ExecutablePath);
    }

    [TestMethod]
    public void Find_HonorsCodexCliPathBeforeDiscoveredLocations()
    {
        const string cliPath = @"D:\Codex\codex.exe";
        var locator = new CodexExecutableLocator(path => path == cliPath);

        var result = locator.Find(
            null,
            null,
            null,
            null,
            null,
            cliPath: cliPath);

        Assert.IsNotNull(result);
        Assert.AreEqual(cliPath, result.ExecutablePath);
    }
}
