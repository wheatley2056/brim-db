using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Brim.Bahn;

/// <summary>
/// Fahrplan über transport.rest (<c>v6.db.transport.rest</c>, ohne Schlüssel). Liefert Abfahrten mit Echtzeit und
/// Verbindungen mit Umstiegen. Der öffentliche Server ist ein Freiwilligenprojekt und erlaubt 100 Anfragen pro Minute.
/// </summary>
public sealed class TransportRestSource(HttpClient http, string server) : IBahnSource
{
    public const string DefaultServer = "https://v6.db.transport.rest";

    private readonly string _server = (string.IsNullOrWhiteSpace(server) ? DefaultServer : server.Trim()).TrimEnd('/');

    public string Name => "transport.rest";

    public async Task<Station?> FindStationAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{_server}/locations?query={Uri.EscapeDataString(query)}&results=3&fuzzy=true&stops=true&addresses=false&poi=false&linesOfStops=false&language=de";
        using var json = await GetAsync(url, cancellationToken);
        return ParseStation(json.RootElement);
    }

    public async Task<IReadOnlyList<Departure>> DeparturesAsync(Station station, DateTimeOffset from, TimeSpan duration, bool trainsOnly, CancellationToken cancellationToken)
    {
        var url = $"{_server}/stops/{Uri.EscapeDataString(station.Id)}/departures?when={Uri.EscapeDataString(from.ToString("o", CultureInfo.InvariantCulture))}"
            + $"&duration={(int)Math.Ceiling(duration.TotalMinutes)}&results=40&remarks=false&linesOfStops=false&language=de";
        if (trainsOnly)
        {
            url += "&bus=false&tram=false&subway=false&ferry=false&taxi=false";
        }
        using var json = await GetAsync(url, cancellationToken);
        return ParseDepartures(json.RootElement);
    }

    public async Task<IReadOnlyList<Connection>> ConnectionsAsync(Station from, Station to, DateTimeOffset departure, CancellationToken cancellationToken)
    {
        var url = $"{_server}/journeys?from={Uri.EscapeDataString(from.Id)}&to={Uri.EscapeDataString(to.Id)}"
            + $"&departure={Uri.EscapeDataString(departure.ToString("o", CultureInfo.InvariantCulture))}&results=5&stopovers=false&remarks=false&language=de";
        using var json = await GetAsync(url, cancellationToken);
        return ParseJourneys(json.RootElement);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new BahnSourceException(response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "transport.rest: zu viele Anfragen, gleich noch mal",
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
                    $"transport.rest antwortet gerade nicht ({(int)response.StatusCode})",
                HttpStatusCode.NotFound => "transport.rest kennt diesen Bahnhof nicht",
                _ => $"transport.rest meldet Fehler {(int)response.StatusCode}",
            });
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new BahnSourceException("transport.rest hat unlesbare Daten geschickt", ex);
        }
    }

    // ───────── Antworten lesen (öffentlich für Tests) ─────────

    public static Station? ParseStation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in root.EnumerateArray())
        {
            var type = Str(item, "type");
            if (type is "stop" or "station" && Str(item, "id") is { Length: > 0 } id && Str(item, "name") is { Length: > 0 } name)
            {
                return new Station(id, name);
            }
        }
        return null;
    }

    /// <summary>v6 schickt <c>{ "departures": [...] }</c>, v5 nur das Array.</summary>
    public static IReadOnlyList<Departure> ParseDepartures(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("departures", out var list) ? list : root;
        var departures = new List<Departure>();
        if (items.ValueKind != JsonValueKind.Array)
        {
            return departures;
        }
        foreach (var item in items.EnumerateArray())
        {
            if (ParseDeparture(item, "when", "plannedWhen", "delay", "platform", "plannedPlatform") is { } departure)
            {
                departures.Add(departure with { Direction = Str(item, "direction") ?? "" });
            }
        }
        departures.Sort((a, b) => a.When.CompareTo(b.When));
        return departures;
    }

    public static IReadOnlyList<Connection> ParseJourneys(JsonElement root)
    {
        var connections = new List<Connection>();
        if (!root.TryGetProperty("journeys", out var journeys) || journeys.ValueKind != JsonValueKind.Array)
        {
            return connections;
        }
        foreach (var journey in journeys.EnumerateArray())
        {
            if (!journey.TryGetProperty("legs", out var legsElement) || legsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            // Fußwege zählen nicht als Umstieg und haben keine Linie.
            var legs = legsElement.EnumerateArray().Where(l => !Bool(l, "walking")).ToList();
            if (legs.Count == 0)
            {
                continue;
            }
            var first = legs[0];
            var last = legs[^1];
            if (ParseDeparture(first, "departure", "plannedDeparture", "departureDelay", "departurePlatform", "plannedDeparturePlatform") is not { } departure)
            {
                continue;
            }
            var direction = Str(first, "direction") ?? (first.TryGetProperty("destination", out var destination) ? Str(destination, "name") : null) ?? "";
            var plannedArrival = Time(last, "plannedArrival");
            connections.Add(new Connection
            {
                Departure = departure with
                {
                    Direction = direction,
                    TripId = Str(journey, "refreshToken") is { Length: > 0 } token ? token : departure.TripId,
                    Cancelled = legs.Any(l => Bool(l, "cancelled")),
                },
                PlannedArrival = plannedArrival,
                ActualArrival = Num(last, "arrivalDelay") is not null ? Time(last, "arrival") : null,
                Changes = legs.Count - 1,
            });
        }
        return connections;
    }

    private static Departure? ParseDeparture(JsonElement item, string whenKey, string plannedKey, string delayKey, string platformKey, string plannedPlatformKey)
    {
        if (Time(item, plannedKey) is not { } planned)
        {
            return null;
        }
        var line = item.TryGetProperty("line", out var lineElement) && lineElement.ValueKind == JsonValueKind.Object
            ? Str(lineElement, "name") ?? Str(lineElement, "productName") ?? "Zug"
            : "Zug";
        // Ohne Echtzeit ist „delay“ null; „when“ ist dann nur der Plan.
        var realtime = Num(item, delayKey) is not null;
        var cancelled = Bool(item, "cancelled");
        return new Departure
        {
            TripId = Str(item, "tripId") ?? $"{line}|{planned:O}",
            Line = Views.CompactLine(line),
            Planned = planned,
            Actual = realtime && !cancelled ? Time(item, whenKey) : null,
            PlannedPlatform = Str(item, plannedPlatformKey),
            Platform = realtime ? Str(item, platformKey) : null,
            Cancelled = cancelled,
        };
    }

    private static string? Str(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Num(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool Bool(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement item, string key) =>
        Str(item, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}
