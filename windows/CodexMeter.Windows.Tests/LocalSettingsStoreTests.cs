using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class LocalSettingsStoreTests
{
    [TestMethod]
    public void Settings_DefaultsAndUpdatesPersistAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new LocalSettingsStore(path);
            var defaults = store.Read(value => value);
            Assert.IsFalse(defaults.NotificationsEnabled);
            Assert.IsTrue(defaults.NotifyThresholds);
            Assert.IsTrue(defaults.NotifyExhausted);
            Assert.IsTrue(defaults.NotifyRestored);
            Assert.IsTrue(defaults.TrayPercentageVisible);

            store.Update(value =>
            {
                value.NotificationsEnabled = true;
                value.NotifyThresholds = false;
                value.NotificationPromptSeen = true;
                value.DeliveredEvents.Add("primary:123:threshold:20");
            });

            var restored = new LocalSettingsStore(path);
            Assert.IsTrue(restored.Read(value => value.NotificationsEnabled));
            Assert.IsFalse(restored.Read(value => value.NotifyThresholds));
            Assert.IsTrue(restored.Read(value => value.NotificationPromptSeen));
            Assert.IsTrue(restored.Read(value => value.DeliveredEvents.Contains("primary:123:threshold:20")));
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count(), "原子写入不应遗留临时文件");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Settings_InvalidJsonFallsBackToSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{invalid");
            var store = new LocalSettingsStore(path);
            Assert.IsFalse(store.Read(value => value.NotificationsEnabled));
            Assert.IsTrue(store.Read(value => value.TrayPercentageVisible));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
