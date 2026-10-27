using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Bahn.Tests;

internal sealed class RecordingSink : INotchSink
{
    public List<ActivityParams> Activities { get; } = [];
    public List<string> Ended { get; } = [];
    public List<HudParams> Huds { get; } = [];
    public List<(string Id, TileSize Size, Widget Content)> Widgets { get; } = [];
    public List<string> Logs { get; } = [];

    public ActivityParams? Last => Activities.Count > 0 ? Activities[^1] : null;

    public Widget? LastWidget(string id, TileSize size) => Widgets.LastOrDefault(w => w.Id == id && w.Size == size).Content;

    public Task SetActivityAsync(ActivityParams activity)
    {
        Activities.Add(activity);
        return Task.CompletedTask;
    }

    public Task EndActivityAsync(string id)
    {
        Ended.Add(id);
        return Task.CompletedTask;
    }

    public Task ShowHudAsync(HudParams hud)
    {
        Huds.Add(hud);
        return Task.CompletedTask;
    }

    public Task LogAsync(string message, string level)
    {
        Logs.Add(message);
        return Task.CompletedTask;
    }

    public Task SetWidgetAsync(string id, TileSize size, Widget content)
    {
        Widgets.Add((id, size, content));
        return Task.CompletedTask;
    }
}

/// <summary>Quelle mit festen Antworten je Bahnhofsname; zählt die Abrufe.</summary>
internal sealed class FakeSource : IBahnSource
{
    public string Name => "fake";
    public Dictionary<string, List<Departure>> Departures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Connection> Connections { get; set; } = [];
    public Exception? Failure { get; set; }
    public int DepartureCalls { get; private set; }
    public int ConnectionCalls { get; private set; }
    public DateTimeOffset? LastFrom { get; private set; }

    public Task<Station?> FindStationAsync(string query, CancellationToken cancellationToken) =>
        Task.FromResult(query.StartsWith("Nirgendwo", StringComparison.Ordinal) ? null : new Station("id:" + query, query + " Hbf"));

    public Task<IReadOnlyList<Departure>> DeparturesAsync(Station station, DateTimeOffset from, TimeSpan duration, bool trainsOnly, CancellationToken cancellationToken)
    {
        DepartureCalls++;
        LastFrom = from;
        if (Failure is { } failure)
        {
            throw failure;
        }
        var name = station.Id["id:".Length..];
        return Task.FromResult<IReadOnlyList<Departure>>(Departures.GetValueOrDefault(name) ?? []);
    }

    public Task<IReadOnlyList<Connection>> ConnectionsAsync(Station from, Station to, DateTimeOffset departure, CancellationToken cancellationToken)
    {
        ConnectionCalls++;
        if (Failure is { } failure)
        {
            throw failure;
        }
        return Task.FromResult<IReadOnlyList<Connection>>(Connections);
    }
}

/// <summary>Antwortet je Pfadanfang mit Inhalt und Status; merkt sich die Anfragen.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<(string Prefix, HttpStatusCode Status, string Body)> Routes { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!.ToString();
        foreach (var (prefix, status, body) in Routes)
        {
            if (url.Contains(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal static class Test
{
    /// <summary>Montag, 26.10.2026, 16:30 in Freiburg (MEZ).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 16, 30, 0, TimeSpan.FromHours(1));

    public static void Setup()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Views.Zone = TimetablesSource.Berlin;
    }

    public static string Data(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name));

    public static DateTimeOffset At(int hour, int minute) => new(2026, 10, 26, hour, minute, 0, TimeSpan.FromHours(1));

    public static Departure Dep(string line, int hour, int minute, int? delay = 0, string platform = "3", string? actualPlatform = null,
        bool cancelled = false, string direction = "Offenburg", string? trip = null) => new()
    {
        TripId = trip ?? $"{line}-{hour}{minute}",
        Line = line,
        Direction = direction,
        Planned = At(hour, minute),
        Actual = delay is { } d && !cancelled ? At(hour, minute).AddMinutes(d) : null,
        PlannedPlatform = platform,
        Platform = delay is null ? null : actualPlatform ?? platform,
        Cancelled = cancelled,
    };

    private static readonly JsonSerializerOptions Readable = new(PluginJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lesbar statt · usw., damit die Tests nach Text suchen können.</summary>
    public static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), Readable);
}
