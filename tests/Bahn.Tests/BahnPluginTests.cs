using Brim.Core;
using Brim.Core.Plugins;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Bahn.Tests;

public sealed class BahnPluginTests
{
    private readonly RecordingSink _sink = new();
    private readonly FakeSource _source = new();
    private readonly FakeTimeProvider _time = new(Test.Now);
    private int _sourcesCreated;

    public BahnPluginTests() => Test.Setup();

    private BahnPlugin Create(PluginSettings? settings = null) => new(_sink, _ =>
    {
        _sourcesCreated++;
        return _source;
    }, _time, settings ?? new PluginSettings { Stations = ["Freiburg"] });

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TileSize>> DeparturesLarge =
        new Dictionary<string, IReadOnlyList<TileSize>> { ["departures"] = [TileSize.Large] };

    [Fact]
    public async Task Next_train_shows_in_the_notch_with_line_and_minutes()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42), Test.Dep("S 1", 16, 55)];
        var plugin = Create();

        await plugin.TickAsync();

        var activity = Assert.IsType<ActivityParams>(_sink.Last);
        Assert.Equal(Views.ActivityId, activity.Id);
        Assert.False(activity.Background);
        Assert.Contains("RE 7 · 12 min", Test.Json(activity.Trailing!));
        Assert.Contains("Freiburg Hbf", Test.Json(activity.Expanded!));
    }

    [Fact]
    public async Task Train_far_away_stays_in_the_background()
    {
        _source.Departures["Freiburg"] = [Test.Dep("ICE 104", 17, 20)];
        var plugin = Create(new PluginSettings { Stations = ["Freiburg"], NotchWindow = TimeSpan.FromMinutes(30) });

        await plugin.TickAsync();

        Assert.True(_sink.Last!.Background);
    }

    [Fact]
    public async Task Trains_you_cannot_reach_are_skipped()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 34), Test.Dep("S 1", 16, 45)];
        var plugin = Create(new PluginSettings { Stations = ["Freiburg"], Walk = TimeSpan.FromMinutes(10) });

        await plugin.TickAsync();

        Assert.Equal(Test.Now.AddMinutes(10), _source.LastFrom);
        Assert.Equal("S 1", plugin.NextDeparture(Test.Now)!.Line);
        Assert.Contains("S 1 · 15 min", Test.Json(_sink.Last!.Trailing!));
    }

    [Fact]
    public async Task Without_notch_and_widgets_nothing_is_fetched()
    {
        var plugin = Create(new PluginSettings { Stations = ["Freiburg"], Notch = false });

        await plugin.TickAsync();

        Assert.Equal(0, _source.DepartureCalls);
        Assert.Empty(_sink.Activities);
    }

    [Fact]
    public async Task Data_is_fetched_once_a_minute_but_drawn_more_often()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42)];
        var plugin = Create();

        await plugin.TickAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await plugin.TickAsync();
        Assert.Equal(1, _source.DepartureCalls);
        // Die Minuten zählen trotzdem herunter.
        Assert.Contains("RE 7 · 11 min", Test.Json(_sink.Last!.Trailing!));

        _time.Advance(TimeSpan.FromSeconds(30));
        await plugin.TickAsync();
        Assert.Equal(2, _source.DepartureCalls);
    }

    [Fact]
    public async Task Platform_change_is_announced_once()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42)];
        var plugin = Create();
        await plugin.TickAsync();
        Assert.Empty(_sink.Huds);

        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42, actualPlatform: "5")];
        _time.Advance(BahnPlugin.RefreshInterval);
        await plugin.TickAsync();
        _time.Advance(BahnPlugin.RefreshInterval);
        await plugin.TickAsync();

        var hud = Assert.Single(_sink.Huds);
        Assert.Contains("heute von Gleis 5 statt 3", hud.Announcement);
        Assert.Contains("\"color\":\"" + BrimColors.Warning + "\"", Test.Json(_sink.Last!.Trailing!));
    }

    [Fact]
    public async Task Cancellation_and_growing_delay_are_announced()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42, delay: 3)];
        var plugin = Create();
        await plugin.TickAsync();
        Assert.Empty(_sink.Huds);

        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42, delay: 6)];
        _time.Advance(BahnPlugin.RefreshInterval);
        await plugin.TickAsync();
        Assert.Contains("+6 min, fährt um 16:48", _sink.Huds[^1].Announcement);

        // Ein bis vier Minuten mehr sind keine neue Meldung wert.
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42, delay: 9)];
        _time.Advance(BahnPlugin.RefreshInterval);
        await plugin.TickAsync();
        Assert.Single(_sink.Huds);

        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42, cancelled: true)];
        _time.Advance(BahnPlugin.RefreshInterval);
        await plugin.TickAsync();
        Assert.Equal(2, _sink.Huds.Count);
        Assert.Contains("RE 7 16:42 nach Offenburg: fällt aus", _sink.Huds[^1].Announcement);
        Assert.Contains("RE 7 fällt aus", Test.Json(_sink.Last!.Trailing!));
    }

    [Fact]
    public async Task No_alerts_when_switched_off_or_train_is_far_away()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42, cancelled: true), Test.Dep("ICE 104", 17, 40, cancelled: true)];
        var plugin = Create(new PluginSettings { Stations = ["Freiburg"], Alerts = false });
        await plugin.TickAsync();
        Assert.Empty(_sink.Huds);

        var far = new RecordingSink();
        var other = new BahnPlugin(far, _ => _source, _time, new PluginSettings { Stations = ["Freiburg"], Walk = TimeSpan.FromMinutes(20) });
        await other.TickAsync();
        Assert.Empty(far.Huds);
    }

    [Fact]
    public async Task Placing_the_widget_fetches_all_stations_at_once()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42)];
        _source.Departures["Offenburg"] = [Test.Dep("IC 2005", 16, 50, direction: "Karlsruhe Hbf")];
        var plugin = Create(new PluginSettings { Stations = ["Freiburg", "Offenburg"], Notch = false });

        await plugin.SetPlacedWidgetsAsync(DeparturesLarge);

        Assert.Equal(2, _source.DepartureCalls);
        var widget = Test.Json(_sink.LastWidget("departures", TileSize.Large)!);
        Assert.Contains("Freiburg Hbf", widget);
        Assert.Contains("1/2", widget);
        Assert.Contains(Views.NextStationAction, widget);

        await plugin.HandleActionAsync(Views.NextStationAction);
        widget = Test.Json(_sink.LastWidget("departures", TileSize.Large)!);
        Assert.Contains("Offenburg Hbf", widget);
        Assert.Contains("Karlsruhe Hbf", widget);
        Assert.Contains("2/2", widget);
    }

    [Fact]
    public async Task Errors_keep_the_last_departures_and_say_offline()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42)];
        var plugin = Create();
        await plugin.SetPlacedWidgetsAsync(DeparturesLarge);

        _source.Failure = new BahnSourceException("transport.rest antwortet gerade nicht (503)");
        _time.Advance(BahnPlugin.RefreshInterval);
        await plugin.TickAsync();

        var widget = Test.Json(_sink.LastWidget("departures", TileSize.Large)!);
        Assert.Contains("RE 7", widget);
        Assert.Contains("offline", widget);
        Assert.Contains(_sink.Logs, l => l.Contains("503"));
    }

    [Fact]
    public async Task First_error_without_data_shows_the_reason()
    {
        _source.Failure = new HttpRequestException("kein Netz");
        var plugin = Create();

        await plugin.SetPlacedWidgetsAsync(DeparturesLarge);

        Assert.Contains("keine Verbindung", Test.Json(_sink.LastWidget("departures", TileSize.Large)!));
    }

    [Fact]
    public async Task Unknown_station_says_so()
    {
        var plugin = Create(new PluginSettings { Stations = ["Nirgendwo"] });

        await plugin.SetPlacedWidgetsAsync(DeparturesLarge);

        Assert.Contains("Bahnhof „Nirgendwo“ nicht gefunden", Test.Json(_sink.LastWidget("departures", TileSize.Large)!));
    }

    [Fact]
    public async Task Commute_drives_the_notch_and_its_widget()
    {
        _source.Departures["Freiburg"] = [Test.Dep("S 1", 16, 35)];
        _source.Connections = [new Connection { Departure = Test.Dep("RE 7", 16, 40, delay: 2), PlannedArrival = Test.At(17, 8), Changes = 0 }];
        var plugin = Create(new PluginSettings { Stations = ["Freiburg"], CommuteFrom = "Freiburg", CommuteTo = "Offenburg" });

        await plugin.SetPlacedWidgetsAsync(new Dictionary<string, IReadOnlyList<TileSize>> { ["commute"] = [TileSize.Medium] });

        Assert.Contains("RE 7 · 12 min", Test.Json(_sink.Last!.Trailing!));
        var widget = Test.Json(_sink.LastWidget("commute", TileSize.Medium)!);
        Assert.Contains("Freiburg Hbf → Offenburg Hbf", widget);
        Assert.Contains("an 17:08 · direkt", widget);
        // Ohne Widget „Abfahrten“ braucht es den Bahnhof nicht.
        Assert.Equal(0, _source.DepartureCalls);
    }

    [Fact]
    public async Task Train_gone_ends_the_activity()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 32)];
        var plugin = Create();
        await plugin.TickAsync();
        Assert.Single(_sink.Activities);

        _time.Advance(TimeSpan.FromMinutes(3));
        _source.Departures["Freiburg"] = [];
        await plugin.TickAsync();

        Assert.Equal([Views.ActivityId], _sink.Ended);
    }

    [Fact]
    public async Task New_keys_switch_the_source_and_search_stations_again()
    {
        var plugin = Create();
        await plugin.TickAsync();
        Assert.Equal(1, _sourcesCreated);

        await plugin.ApplySettingsAsync(new PluginSettings { Stations = ["Freiburg"], DbClientId = "a", DbApiKey = "b" });

        Assert.Equal(2, _sourcesCreated);
        Assert.Equal(2, _source.DepartureCalls);
    }

    [Fact]
    public async Task Nothing_is_sent_twice()
    {
        _source.Departures["Freiburg"] = [Test.Dep("RE 7", 16, 42)];
        var plugin = Create();
        await plugin.SetPlacedWidgetsAsync(DeparturesLarge);
        var activities = _sink.Activities.Count;
        var widgets = _sink.Widgets.Count;

        await plugin.TickAsync();

        Assert.Equal(activities, _sink.Activities.Count);
        Assert.Equal(widgets, _sink.Widgets.Count);
    }
}
