using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed record RefreshPreferences(int LiveSeconds = 2, int GeneralSeconds = 30, int SshSeconds = 60)
{
    public static readonly int[] LiveOptions = [1, 2, 5, 10];
    public static readonly int[] GeneralOptions = [15, 30, 60, 120];
    public static readonly int[] SshOptions = [30, 60, 120, 300];

    public static RefreshPreferences From(AppSettings settings) => new(
        Validated(settings.LiveRefreshIntervalSeconds, LiveOptions, 2),
        Validated(settings.GeneralRefreshIntervalSeconds, GeneralOptions, 30),
        Validated(settings.SshRefreshIntervalSeconds, SshOptions, 60));

    public RefreshPreferences Update(JsonObject values) => new(
        ReadAllowed(values["liveSeconds"], LiveOptions, LiveSeconds),
        ReadAllowed(values["generalSeconds"], GeneralOptions, GeneralSeconds),
        ReadAllowed(values["sshSeconds"], SshOptions, SshSeconds));

    public void Persist(AppSettings settings)
    {
        settings.LiveRefreshIntervalSeconds = LiveSeconds;
        settings.GeneralRefreshIntervalSeconds = GeneralSeconds;
        settings.SshRefreshIntervalSeconds = SshSeconds;
    }

    public JsonObject Payload(bool sshEnabled) => new()
    {
        ["supported"] = true,
        ["liveSeconds"] = LiveSeconds,
        ["generalSeconds"] = GeneralSeconds,
        ["sshSeconds"] = SshSeconds,
        ["liveOptions"] = new JsonArray(LiveOptions.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["generalOptions"] = new JsonArray(GeneralOptions.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["sshOptions"] = new JsonArray(SshOptions.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["sshEnabled"] = sshEnabled
    };

    private static int ReadAllowed(JsonNode? node, int[] allowed, int fallback) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? Validated(number, allowed, fallback) : fallback;

    private static int Validated(int value, int[] allowed, int fallback) => allowed.Contains(value) ? value : fallback;
}
