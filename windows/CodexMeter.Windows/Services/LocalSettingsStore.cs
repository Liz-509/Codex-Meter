using System.IO;
using System.Text.Json;

namespace CodexMeter.Windows.Services;

internal sealed class AppSettings
{
    public bool NotificationsEnabled { get; set; }
    public bool NotifyThresholds { get; set; } = true;
    public bool NotifyExhausted { get; set; } = true;
    public bool NotifyRestored { get; set; } = true;
    public bool NotificationPromptSeen { get; set; }
    public bool TrayPercentageVisible { get; set; } = true;
    public HashSet<string> DeliveredEvents { get; set; } = [];
    public Dictionary<string, double> PreviousRemaining { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double?> PreviousResetsAt { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class LocalSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private AppSettings _settings;

    public LocalSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataDirectory, "settings.json");
        _settings = Load(_path);
    }

    public static string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Meter");

    public T Read<T>(Func<AppSettings, T> reader)
    {
        lock (_gate) return reader(_settings);
    }

    public void Update(Action<AppSettings> update)
    {
        lock (_gate)
        {
            update(_settings);
            try { Persist(_path, _settings); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions) ?? new AppSettings();
            settings.DeliveredEvents ??= [];
            settings.PreviousRemaining ??= new(StringComparer.Ordinal);
            settings.PreviousResetsAt ??= new(StringComparer.Ordinal);
            return settings;
        }
        catch (IOException) { return new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
        catch (UnauthorizedAccessException) { return new AppSettings(); }
    }

    private static void Persist(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
