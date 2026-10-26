using System.Globalization;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;

namespace Brim.Bahn;

/// <summary>
/// Holt jede Minute die Abfahrten der Lieblingsbahnhöfe und die Verbindungen der Pendelstrecke, zeigt den nächsten
/// erreichbaren Zug in der Notch und die Tafeln als Widgets. Ändert sich am nächsten Zug etwas Wichtiges (Ausfall,
/// Gleiswechsel, deutlich mehr Verspätung), meldet es sich kurz. Schickt nur, was sich geändert hat.
/// </summary>
public sealed class BahnPlugin
{
    /// <summary>So oft wird neu gezeichnet (Minuten bis zur Abfahrt).</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>So oft kommen neue Daten; transport.rest erlaubt 100 Anfragen pro Minute, die DB-API 60.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>So weit voraus werden Abfahrten geholt.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromMinutes(90);

    /// <summary>Ab so viel zusätzlicher Verspätung meldet sich das Plugin.</summary>
    public const int AlertDelayStep = 5;

    private readonly INotchSink _sink;
    private readonly Func<PluginSettings, IBahnSource> _createSource;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _sent = [];
    private readonly Dictionary<string, Station?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StationBoard> _boards = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<TileSize>> _placed = new Dictionary<string, IReadOnlyList<TileSize>>();
    private DateTimeOffset? _fetchedAt;
    private bool _activityShown;
    private Seen? _seen;

    /// <summary>Was zum nächsten Zug schon bekannt (oder gemeldet) ist.</summary>
    private sealed record Seen(string TripId, bool Cancelled, string? Platform, int Delay);

    public BahnPlugin(INotchSink sink, Func<PluginSettings, IBahnSource> createSource, TimeProvider time, PluginSettings settings)
    {
        _sink = sink;
        _createSource = createSource;
        _time = time;
        Settings = settings;
        Source = createSource(settings);
    }

    public PluginSettings Settings { get; private set; }

    public IBahnSource Source { get; private set; }

    /// <summary>Welcher Lieblingsbahnhof im Widget „Abfahrten“ steht (Knopf zum Weiterblättern).</summary>
    public int SelectedStation { get; private set; }

    public CommuteBoard? Commute { get; private set; }

    /// <summary>Die Tafeln in der Reihenfolge der Einstellungen; Bahnhöfe ohne Daten fehlen.</summary>
    public IReadOnlyList<StationBoard> Boards => Settings.Stations.Select(s => _boards.GetValueOrDefault(s)).OfType<StationBoard>().ToList();

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    private bool Placed(string widget) => _placed.GetValueOrDefault(widget) is { Count: > 0 };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(Tick, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_fetchedAt is not { } fetched || now - fetched >= RefreshInterval)
            {
                await FetchAsync(now, cancellationToken);
            }
            await RenderAsync(now);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────── Daten ─────────

    private async Task FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _fetchedAt = now;
        var from = now + Settings.Walk;

        // Nur holen, was jemand sieht: alle Bahnhöfe für das Widget, sonst nur den ersten für die Notch.
        var stations = Placed("departures") ? Settings.Stations
            : Settings.Notch && !Settings.HasCommute ? Settings.Stations.Take(1)
            : [];
        foreach (var query in stations)
        {
            var old = _boards.GetValueOrDefault(query);
            try
            {
                var station = await ResolveAsync(query, cancellationToken);
                var departures = await Source.DeparturesAsync(station, from, Lookahead, Settings.TrainsOnly, cancellationToken);
                _boards[query] = new StationBoard(station, departures, now, null);
            }
            catch (Exception ex) when (IsSourceError(ex, cancellationToken))
            {
                await _sink.LogAsync($"{query}: {ex.Message}", "warn");
                _boards[query] = old is null
                    ? new StationBoard(_resolved.GetValueOrDefault(query) ?? new Station(query, query), [], null, Message(ex))
                    : old with { Error = Message(ex) };
            }
        }

        if (Settings.HasCommute && (Placed("commute") || Settings.Notch))
        {
            try
            {
                var origin = await ResolveAsync(Settings.CommuteFrom, cancellationToken);
                var destination = await ResolveAsync(Settings.CommuteTo, cancellationToken);
                var connections = await Source.ConnectionsAsync(origin, destination, from, cancellationToken);
                Commute = new CommuteBoard(origin, destination, connections, now, null);
            }
            catch (Exception ex) when (IsSourceError(ex, cancellationToken))
            {
                await _sink.LogAsync($"Pendelstrecke: {ex.Message}", "warn");
                Commute = Commute is { } old
                    ? old with { Error = Message(ex) }
                    : new CommuteBoard(new Station(Settings.CommuteFrom, Settings.CommuteFrom), new Station(Settings.CommuteTo, Settings.CommuteTo), [], null, Message(ex));
            }
        }
    }

    /// <summary>Name → Bahnhof, einmal pro Name und Quelle. Nicht gefundene merkt es sich auch, bis sich die Einstellungen ändern.</summary>
    private async Task<Station> ResolveAsync(string query, CancellationToken cancellationToken)
    {
        if (!_resolved.TryGetValue(query, out var station))
        {
            station = await Source.FindStationAsync(query, cancellationToken);
            _resolved[query] = station;
            await _sink.LogAsync(station is null ? $"Bahnhof nicht gefunden: {query}" : $"Bahnhof {query}: {station.Name} ({station.Id})", "info");
        }
        return station ?? throw new BahnSourceException($"Bahnhof „{query}“ nicht gefunden");
    }

    private static bool IsSourceError(Exception ex, CancellationToken cancellationToken) =>
        ex is BahnSourceException or HttpRequestException or JsonException or System.Xml.XmlException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Message(Exception ex) => ex switch
    {
        BahnSourceException => ex.Message,
        OperationCanceledException => "keine Antwort",
        HttpRequestException => "keine Verbindung",
        _ => "unlesbare Daten",
    };

    /// <summary>Der nächste Zug, den man noch schafft: von der Pendelstrecke, sonst vom ersten Bahnhof.</summary>
    public Departure? NextDeparture(DateTimeOffset now) => Reachable(
        Settings.HasCommute ? Commute?.Connections.Select(c => c.Departure) ?? [] : Boards.FirstOrDefault()?.Departures ?? [], now)
        .FirstOrDefault();

    /// <summary>Abfahrten ab jetzt plus Weg; ausgefallene Züge zählen mit ihrer Planzeit.</summary>
    public IEnumerable<Departure> Reachable(IEnumerable<Departure> departures, DateTimeOffset now) =>
        departures.Where(d => d.When >= now + Settings.Walk - TimeSpan.FromSeconds(30));

    // ───────── Anzeigen ─────────

    private async Task RenderAsync(DateTimeOffset now)
    {
        var next = NextDeparture(now);
        await AlertAsync(next, now);

        var stationBoard = SelectedBoard();
        if (Settings.Notch && next is not null)
        {
            var expanded = Views.Expanded(Board(Boards.FirstOrDefault(), now), Settings.HasCommute ? CommuteFor(now) : null, now, Culture);
            var activity = Views.Activity(next, Settings.HasCommute ? Commute?.From.Name : Boards.FirstOrDefault()?.Station.Name,
                next.When - now > Settings.NotchWindow, expanded, now, Culture);
            if (Changed("activity", activity))
            {
                await _sink.SetActivityAsync(activity);
                _activityShown = true;
            }
        }
        else if (_activityShown)
        {
            await _sink.EndActivityAsync(Views.ActivityId);
            _sent.Remove("activity");
            _activityShown = false;
        }

        foreach (var size in _placed.GetValueOrDefault("departures") ?? [])
        {
            var widget = Views.DeparturesWidget(Board(stationBoard, now), SelectedStation, Settings.Stations.Count, size, now, Culture);
            if (Changed("departures:" + size, widget))
            {
                await _sink.SetWidgetAsync("departures", size, widget);
            }
        }
        foreach (var size in _placed.GetValueOrDefault("commute") ?? [])
        {
            var widget = Views.CommuteWidget(CommuteFor(now), Settings.HasCommute, size, now, Culture);
            if (Changed("commute:" + size, widget))
            {
                await _sink.SetWidgetAsync("commute", size, widget);
            }
        }
    }

    private StationBoard? SelectedBoard()
    {
        if (Settings.Stations.Count == 0)
        {
            return null;
        }
        SelectedStation = Math.Clamp(SelectedStation, 0, Settings.Stations.Count - 1);
        return _boards.GetValueOrDefault(Settings.Stations[SelectedStation])
            ?? new StationBoard(new Station(Settings.Stations[SelectedStation], Settings.Stations[SelectedStation]), [], null, null);
    }

    /// <summary>Die Tafel ohne Züge, die man nicht mehr schafft.</summary>
    private StationBoard? Board(StationBoard? board, DateTimeOffset now) =>
        board is null ? null : board with { Departures = Reachable(board.Departures, now).ToList() };

    private CommuteBoard? CommuteFor(DateTimeOffset now) => Commute is not { } commute ? null
        : commute with { Connections = commute.Connections.Where(c => Reachable([c.Departure], now).Any()).ToList() };

    /// <summary>
    /// Meldet Ausfall, Gleiswechsel und jede weitere Verspätung ab <see cref="AlertDelayStep"/> Minuten, sobald der
    /// nächste Zug im Fenster der Notch liegt. Was beim ersten Blick schon so ist, meldet es auch.
    /// </summary>
    private async Task AlertAsync(Departure? next, DateTimeOffset now)
    {
        if (next is null || next.When - now > Settings.NotchWindow)
        {
            return;
        }
        var seen = _seen?.TripId == next.TripId ? _seen : new Seen(next.TripId, false, next.PlannedPlatform, 0);
        var parts = new List<string>();
        if (next.Cancelled && !seen.Cancelled)
        {
            parts.Add("fällt aus");
        }
        if (!next.Cancelled && next.PlatformChanged && !string.Equals(next.Platform, seen.Platform, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"heute von Gleis {next.Platform} statt {next.PlannedPlatform}");
        }
        var delay = seen.Delay;
        if (!next.Cancelled && next.DelayMinutes is { } d && d >= seen.Delay + AlertDelayStep)
        {
            parts.Add($"+{d} min, fährt um {Views.Clock(next.When, Culture)}");
            delay = d;
        }
        _seen = new Seen(next.TripId, next.Cancelled, next.ShownPlatform, delay);
        if (parts.Count > 0 && Settings.Alerts && Settings.Notch)
        {
            await _sink.ShowHudAsync(Views.AlertHud(next, string.Join(" · ", parts), Culture));
        }
    }

    /// <summary>Merkt sich, was zuletzt geschickt wurde; <c>true</c>, wenn <paramref name="value"/> neu ist.</summary>
    private bool Changed(string key, object value)
    {
        var json = JsonSerializer.Serialize(value, value.GetType(), PluginJson.Options);
        if (_sent.TryGetValue(key, out var sent) && sent == json)
        {
            return false;
        }
        _sent[key] = json;
        return true;
    }

    // ───────── Ereignisse von Brim ─────────

    public async Task SetPlacedWidgetsAsync(IReadOnlyDictionary<string, IReadOnlyList<TileSize>> placed)
    {
        await _gate.WaitAsync();
        try
        {
            var wanted = !Placed("departures") && placed.GetValueOrDefault("departures") is { Count: > 0 }
                || !Placed("commute") && placed.GetValueOrDefault("commute") is { Count: > 0 };
            _placed = placed;
            var now = _time.GetUtcNow();
            // Neu platziert: sofort holen statt bis zur nächsten Minute „Lädt …“ zu zeigen.
            if (wanted)
            {
                await FetchAsync(now, CancellationToken.None);
            }
            await RenderAsync(now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(PluginSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (settings.DbClientId != Settings.DbClientId || settings.DbApiKey != Settings.DbApiKey || settings.Server != Settings.Server)
            {
                Source = _createSource(settings);
                _boards.Clear();
                Commute = null;
            }
            // Auch nicht gefundene Bahnhöfe neu suchen: vielleicht war nur ein Tippfehler drin.
            _resolved.Clear();
            if (settings.CommuteFrom != Settings.CommuteFrom || settings.CommuteTo != Settings.CommuteTo)
            {
                Commute = null;
            }
            Settings = settings;
            _fetchedAt = null;
            var now = _time.GetUtcNow();
            await FetchAsync(now, CancellationToken.None);
            await RenderAsync(now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleActionAsync(string actionId)
    {
        await _gate.WaitAsync();
        try
        {
            var now = _time.GetUtcNow();
            switch (actionId)
            {
                case Views.NextStationAction when Settings.Stations.Count > 1:
                    SelectedStation = (SelectedStation + 1) % Settings.Stations.Count;
                    break;
                case Views.RefreshAction:
                    await FetchAsync(now, CancellationToken.None);
                    break;
                default:
                    return;
            }
            await RenderAsync(now);
        }
        finally
        {
            _gate.Release();
        }
    }
}
