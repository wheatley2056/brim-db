namespace Brim.Bahn;

/// <summary>Ein Bahnhof: Kennung der Quelle (meist die EVA-Nummer, z. B. 8000107) und Name.</summary>
public sealed record Station(string Id, string Name);

/// <summary>Eine Abfahrt. Ohne Echtzeitdaten sind <see cref="Actual"/> und <see cref="Platform"/> leer.</summary>
public sealed record Departure
{
    /// <summary>Kennung der Fahrt, gleich über mehrere Abrufe (für Meldungen bei Änderungen).</summary>
    public required string TripId { get; init; }

    /// <summary>„RE 7“, „ICE 104“, „S 1“.</summary>
    public required string Line { get; init; }

    public string Direction { get; init; } = "";

    public required DateTimeOffset Planned { get; init; }

    /// <summary>Erwartete Abfahrt laut Echtzeit; <c>null</c>, wenn es keine gibt.</summary>
    public DateTimeOffset? Actual { get; init; }

    public string? PlannedPlatform { get; init; }

    /// <summary>Gleis laut Echtzeit; <c>null</c>, wenn es keine gibt.</summary>
    public string? Platform { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>Wann der Zug fährt: erwartet, sonst geplant.</summary>
    public DateTimeOffset When => Actual ?? Planned;

    /// <summary>Verspätung in ganzen Minuten; <c>null</c> ohne Echtzeit.</summary>
    public int? DelayMinutes => Actual is { } actual ? (int)Math.Round((actual - Planned).TotalMinutes) : null;

    public string? ShownPlatform => Platform ?? PlannedPlatform;

    public bool PlatformChanged => Platform is { Length: > 0 } now && PlannedPlatform is { Length: > 0 } planned
        && !string.Equals(now, planned, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Eine Verbindung der Pendelstrecke: die erste Abfahrt mit Linie und Gleis, dazu die Ankunft am Ziel.</summary>
public sealed record Connection
{
    public required Departure Departure { get; init; }

    public DateTimeOffset? PlannedArrival { get; init; }

    public DateTimeOffset? ActualArrival { get; init; }

    /// <summary>Anzahl Umstiege.</summary>
    public int Changes { get; init; }

    public DateTimeOffset? Arrival => ActualArrival ?? PlannedArrival;
}

/// <summary>Was ein Bahnhof gerade zeigt: Abfahrten und wann sie geholt wurden, oder warum nicht.</summary>
public sealed record StationBoard(Station Station, IReadOnlyList<Departure> Departures, DateTimeOffset? UpdatedAt, string? Error);

/// <summary>Die Pendelstrecke mit ihren nächsten Verbindungen.</summary>
public sealed record CommuteBoard(Station From, Station To, IReadOnlyList<Connection> Connections, DateTimeOffset? UpdatedAt, string? Error);

/// <summary>Woher die Fahrplandaten kommen.</summary>
public interface IBahnSource
{
    /// <summary>Für das Protokoll, z. B. „transport.rest“.</summary>
    string Name { get; }

    /// <summary>Bester Treffer für einen Namen oder eine EVA-Nummer, sonst <c>null</c>.</summary>
    Task<Station?> FindStationAsync(string query, CancellationToken cancellationToken);

    /// <summary>Abfahrten ab <paramref name="from"/> für die nächsten <paramref name="duration"/>, nach Zeit sortiert.</summary>
    Task<IReadOnlyList<Departure>> DeparturesAsync(Station station, DateTimeOffset from, TimeSpan duration, bool trainsOnly, CancellationToken cancellationToken);

    /// <summary>Nächste Verbindungen ab <paramref name="departure"/>.</summary>
    Task<IReadOnlyList<Connection>> ConnectionsAsync(Station from, Station to, DateTimeOffset departure, CancellationToken cancellationToken);
}

/// <summary>Die Quelle hat geantwortet, aber nicht wie erwartet (Fehlercode, kaputte Daten). Die Meldung ist für Nutzer.</summary>
public sealed class BahnSourceException(string message, Exception? inner = null) : Exception(message, inner);
