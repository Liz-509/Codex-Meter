using System.Text;
using System.Text.Json;

namespace CodexMeter.Windows.Services;

internal sealed class JsonLineBuffer
{
    private readonly StringBuilder _pending = new();

    public IReadOnlyList<JsonElement> Append(string chunk)
    {
        if (chunk.Length == 0) return Array.Empty<JsonElement>();
        _pending.Append(chunk);

        var messages = new List<JsonElement>();
        while (TryTakeLine(out var line))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                messages.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // A malformed server line must not prevent later responses from being read.
            }
        }
        return messages;
    }

    private bool TryTakeLine(out string line)
    {
        for (var index = 0; index < _pending.Length; index++)
        {
            if (_pending[index] != '\n') continue;
            line = _pending.ToString(0, index).TrimEnd('\r');
            _pending.Remove(0, index + 1);
            return true;
        }

        line = string.Empty;
        return false;
    }
}
