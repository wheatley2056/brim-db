using System.Globalization;
using System.Text.Json;

namespace Brim.Bahn;

/// <summary>Einstellungen aus <c>plugin.json</c> mit den Werten des Nutzers. Schalter und Zahlen dürfen auch als Text kommen.</summary>
public sealed record PluginSettings
{
    /// <summary>Bahnhöfe, wie der Nutzer sie geschrieben hat (Name oder EVA-Nummer); der erste steht in der Notch.</summary>
    public IReadOnlyList<string> Stations { get; init; } = [];

    public string CommuteFrom { get; init; } = "";

    public string CommuteTo { get; init; } = "";

    /// <summary>Züge, die vorher fahren, schafft man nicht mehr.</summary>
    public TimeSpan Walk { get; init; } = TimeSpan.Zero;

    public bool Notch { get; init; } = true;

    /// <summary>Der nächste Zug kommt in die kompakte Notch, sobald er höchstens so weit weg ist.</summary>
    public TimeSpan NotchWindow { get; init; } = TimeSpan.FromMinutes(30);

    public bool Alerts { get; init; } = true;

    public bool TrainsOnly { get; init; } = true;

    public string DbClientId { get; init; } = "";

    public string DbApiKey { get; init; } = "";

    public string Server { get; init; } = TransportRestSource.DefaultServer;

    public bool HasCommute => CommuteFrom.Length > 0 && CommuteTo.Length > 0;

    /// <summary>Mit Client-ID und API-Key kommen die Daten von der DB selbst, sonst von transport.rest.</summary>
    public bool UsesDbApi => DbClientId.Length > 0 && DbApiKey.Length > 0;

    public static PluginSettings From(IReadOnlyDictionary<string, JsonElement> values)
    {
        var defaults = new PluginSettings();
        return new PluginSettings
        {
            Stations = ParseStations(Text(values, "stations")),
            CommuteFrom = Text(values, "commuteFrom"),
            CommuteTo = Text(values, "commuteTo"),
            Walk = TimeSpan.FromMinutes(Math.Clamp(Number(values, "walkMinutes", 0), 0, 120)),
            Notch = Flag(values, "notch", defaults.Notch),
            NotchWindow = TimeSpan.FromMinutes(Math.Clamp(Number(values, "notchMinutes", defaults.NotchWindow.TotalMinutes), 0, 240)),
            Alerts = Flag(values, "alerts", defaults.Alerts),
            TrainsOnly = Flag(values, "trainsOnly", defaults.TrainsOnly),
            DbClientId = Text(values, "dbClientId"),
            DbApiKey = Text(values, "dbApiKey"),
            Server = Text(values, "server") is { Length: > 0 } server ? server : defaults.Server,
        };
    }

    /// <summary>„Freiburg Hbf, Offenburg; 8000105“ → [Freiburg Hbf, Offenburg, 8000105], ohne doppelte.</summary>
    public static List<string> ParseStations(string? text)
    {
        var stations = new List<string>();
        foreach (var part in (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!stations.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                stations.Add(part);
            }
        }
        return stations;
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    private static double Number(IReadOnlyDictionary<string, JsonElement> values, string key, double fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number => value.GetDouble() != 0,
            _ => fallback,
        };
    }
}
