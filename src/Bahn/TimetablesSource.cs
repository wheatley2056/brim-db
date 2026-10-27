using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace Brim.Bahn;

/// <summary>
/// Fahrplan über die offizielle Timetables-API der Deutschen Bahn (DB API Marketplace, kostenlos mit Client-ID und
/// API-Key). Den Plan gibt es stundenweise, Verspätungen, Ausfälle und Gleiswechsel kommen aus den Änderungen
/// (<c>fchg</c>). Die API kennt nur Abfahrten; als Pendelstrecke zeigt diese Quelle direkte Züge, die am Ziel halten.
/// </summary>
public sealed class TimetablesSource(HttpClient http, string clientId, string apiKey, TimeProvider time) : IBahnSource
{
    public const string BaseUrl = "https://apis.deutschebahn.com/db-api-marketplace/apis/timetables/v1";

    /// <summary>Den Plan einer Stunde holt die Quelle höchstens so oft neu.</summary>
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, (DateTimeOffset FetchedAt, List<PlanStop> Stops)> _plans = [];

    public string Name => "Timetables-API der DB";

    public static TimeZoneInfo Berlin { get; } = FindBerlin();

    /// <summary>Ein Halt aus dem Plan, mit dem weiteren Laufweg für die Pendelstrecke.</summary>
    public sealed record PlanStop(string Id, Departure Departure, IReadOnlyList<string> Path, bool Bus);

    public async Task<Station?> FindStationAsync(string query, CancellationToken cancellationToken)
    {
        var xml = await GetAsync($"/station/{Uri.EscapeDataString(query.Trim())}", cancellationToken);
        return ParseStation(xml);
    }

    public async Task<IReadOnlyList<Departure>> DeparturesAsync(Station station, DateTimeOffset from, TimeSpan duration, bool trainsOnly, CancellationToken cancellationToken)
    {
        var stops = await StopsAsync(station, from, duration, cancellationToken);
        return stops.Where(s => !(trainsOnly && s.Bus)).Select(s => s.Departure).ToList();
    }

    public async Task<IReadOnlyList<Connection>> ConnectionsAsync(Station from, Station to, DateTimeOffset departure, CancellationToken cancellationToken)
    {
        var stops = await StopsAsync(from, departure, TimeSpan.FromMinutes(120), cancellationToken);
        return stops
            .Where(s => s.Path.Any(p => SameStation(p, to.Name)))
            .Select(s => new Connection { Departure = s.Departure })
            .Take(5)
            .ToList();
    }

    private async Task<List<PlanStop>> StopsAsync(Station station, DateTimeOffset from, TimeSpan duration, CancellationToken cancellationToken)
    {
        var until = from + duration;
        var stops = new Dictionary<string, PlanStop>();
        // Der Plan liegt in Stunden der Ortszeit; eine Stunde davor, damit verspätete Züge nicht fehlen.
        var hour = TimeZoneInfo.ConvertTime(from, Berlin).AddHours(-1);
        hour = new DateTimeOffset(hour.Year, hour.Month, hour.Day, hour.Hour, 0, 0, hour.Offset);
        for (; hour < until; hour = hour.AddHours(1))
        {
            foreach (var stop in await PlanAsync(station.Id, TimeZoneInfo.ConvertTime(hour, Berlin), cancellationToken))
            {
                stops[stop.Id] = stop;
            }
        }
        var changes = ParseChanges(await GetAsync($"/fchg/{Uri.EscapeDataString(station.Id)}", cancellationToken));
        return stops.Values
            .Select(s => changes.TryGetValue(s.Id, out var change) ? Apply(s, change) : s)
            .Where(s => s.Departure.When >= from && s.Departure.Planned < until)
            .OrderBy(s => s.Departure.When)
            .ToList();
    }

    private async Task<List<PlanStop>> PlanAsync(string eva, DateTimeOffset hour, CancellationToken cancellationToken)
    {
        var key = $"{eva}/{hour:yyMMdd}/{hour:HH}";
        var now = time.GetUtcNow();
        if (_plans.TryGetValue(key, out var cached) && now - cached.FetchedAt < PlanLifetime)
        {
            return cached.Stops;
        }
        List<PlanStop> stops;
        try
        {
            stops = ParsePlan(await GetAsync("/plan/" + key, cancellationToken));
        }
        catch (BahnSourceException) when (cached.Stops is not null)
        {
            return cached.Stops;
        }
        // Alte Stunden wegwerfen, damit der Speicher nicht wächst.
        foreach (var old in _plans.Where(p => now - p.Value.FetchedAt > TimeSpan.FromHours(6)).Select(p => p.Key).ToList())
        {
            _plans.Remove(old);
        }
        _plans[key] = (now, stops);
        return stops;
    }

    private async Task<XDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Add("DB-Client-Id", clientId);
        request.Headers.Add("DB-Api-Key", apiKey);
        request.Headers.Add("Accept", "application/xml");
        using var response = await http.SendAsync(request, cancellationToken);
        // Für Stunden ohne Fahrplan antwortet die API mit 404; das ist ein leerer Plan, kein Fehler.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new XDocument(new XElement("timetable"));
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new BahnSourceException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "DB-API: Client-ID oder API-Key stimmt nicht, oder die Timetables-API ist nicht abonniert",
                HttpStatusCode.TooManyRequests => "DB-API: zu viele Anfragen, gleich noch mal",
                _ => $"DB-API meldet Fehler {(int)response.StatusCode}",
            });
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BahnSourceException("DB-API hat unlesbare Daten geschickt", ex);
        }
    }

    // ───────── Antworten lesen (öffentlich für Tests) ─────────

    public static Station? ParseStation(XDocument xml) =>
        xml.Descendants("station")
            .Select(s => (Eva: (string?)s.Attribute("eva"), Name: (string?)s.Attribute("name")))
            .Where(s => s.Eva is { Length: > 0 } && s.Name is { Length: > 0 })
            .Select(s => new Station(s.Eva!, s.Name!))
            .FirstOrDefault();

    /// <summary>Halte mit Abfahrt; endende Züge (nur <c>ar</c>) fahren hier nicht mehr ab.</summary>
    public static List<PlanStop> ParsePlan(XDocument xml)
    {
        var stops = new List<PlanStop>();
        foreach (var s in xml.Descendants("s"))
        {
            var id = (string?)s.Attribute("id");
            var dp = s.Element("dp");
            if (id is null || dp is null || ParseTime((string?)dp.Attribute("pt")) is not { } planned)
            {
                continue;
            }
            var tl = s.Element("tl");
            var category = (string?)tl?.Attribute("c") ?? "";
            var path = Path((string?)dp.Attribute("ppth"));
            stops.Add(new PlanStop(id, new Departure
            {
                TripId = id,
                Line = LineName(category, (string?)dp.Attribute("l"), (string?)tl?.Attribute("n")),
                Direction = path.Count > 0 ? path[^1] : "",
                Planned = planned,
                PlannedPlatform = (string?)dp.Attribute("pp"),
            }, path, category.ToUpperInvariant() is "BUS" or "STR" or "U" or "SEV" or "TAXI"));
        }
        return stops;
    }

    /// <summary>Änderung einer Abfahrt: neue Zeit (<c>ct</c>), neues Gleis (<c>cp</c>), Ausfall (<c>cs="c"</c>), neuer Laufweg.</summary>
    public sealed record Change(DateTimeOffset? Time, string? Platform, bool Cancelled, IReadOnlyList<string>? Path);

    public static Dictionary<string, Change> ParseChanges(XDocument xml)
    {
        var changes = new Dictionary<string, Change>();
        foreach (var s in xml.Descendants("s"))
        {
            if ((string?)s.Attribute("id") is not { } id || s.Element("dp") is not { } dp)
            {
                continue;
            }
            var cpth = (string?)dp.Attribute("cpth");
            changes[id] = new Change(ParseTime((string?)dp.Attribute("ct")), (string?)dp.Attribute("cp"),
                (string?)dp.Attribute("cs") == "c", cpth is null ? null : Path(cpth));
        }
        return changes;
    }

    /// <summary>
    /// Wendet eine Änderung an. Steht ein Halt in den Änderungen, gibt es Echtzeit für ihn: Ohne neue Zeit fährt er
    /// pünktlich, ohne neues Gleis vom geplanten.
    /// </summary>
    public static PlanStop Apply(PlanStop stop, Change change)
    {
        var path = change.Path is { Count: > 0 } changed ? changed : stop.Path;
        return stop with
        {
            Path = path,
            Departure = stop.Departure with
            {
                Actual = change.Cancelled ? null : change.Time ?? stop.Departure.Planned,
                Platform = change.Platform ?? stop.Departure.PlannedPlatform,
                Cancelled = change.Cancelled,
                Direction = path.Count > 0 ? path[^1] : stop.Departure.Direction,
            },
        };
    }

    /// <summary>„RE“ + „7“ → „RE 7“; „S“ + „S1“ → „S1“; „ICE“ ohne Linie + Zugnummer 104 → „ICE 104“.</summary>
    public static string LineName(string category, string? line, string? number)
    {
        if (line is { Length: > 0 })
        {
            return line.StartsWith(category, StringComparison.OrdinalIgnoreCase) ? line : $"{category} {line}".Trim();
        }
        return $"{category} {number}".Trim() is { Length: > 0 } name ? name : "Zug";
    }

    /// <summary>„1810261512“ (yyMMddHHmm, Ortszeit) → Zeitpunkt.</summary>
    public static DateTimeOffset? ParseTime(string? text)
    {
        if (text is null || !DateTime.TryParseExact(text, "yyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }
        return new DateTimeOffset(local, Berlin.GetUtcOffset(local));
    }

    private static List<string> Path(string? text) =>
        (text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>„Offenburg“ passt zu „Offenburg“, „Freiburg(Breisgau) Hbf“ zu „Freiburg(Breisgau) Hbf“; Leerzeichen zählen nicht.</summary>
    public static bool SameStation(string a, string b) =>
        string.Equals(Squeeze(a), Squeeze(b), StringComparison.OrdinalIgnoreCase);

    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static TimeZoneInfo FindBerlin()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Local;
    }
}
