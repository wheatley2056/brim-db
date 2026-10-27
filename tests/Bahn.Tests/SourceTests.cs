using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Bahn.Tests;

public sealed class TransportRestTests
{
    public TransportRestTests() => Test.Setup();

    [Fact]
    public void Departures_carry_delay_platform_change_and_cancellation_sorted_by_time()
    {
        using var json = JsonDocument.Parse(Test.Data("departures.json"));

        var departures = TransportRestSource.ParseDepartures(json.RootElement);

        Assert.Equal(["ICE 104", "RE 7", "S 1", "RB 7"], departures.Select(d => d.Line));
        var ice = departures[0];
        Assert.True(ice.PlatformChanged);
        Assert.Equal("5", ice.ShownPlatform);
        Assert.Equal(0, ice.DelayMinutes);
        var re = departures[1];
        Assert.Equal(8, re.DelayMinutes);
        Assert.Equal(Test.At(16, 50), re.When);
        Assert.Equal("Offenburg", re.Direction);
        Assert.False(re.PlatformChanged);
        var s1 = departures[2];
        Assert.True(s1.Cancelled);
        Assert.Null(s1.Actual);
        Assert.Equal("7", s1.ShownPlatform);
        // Ohne „delay“ gibt es keine Echtzeit, auch wenn „when“ gesetzt ist.
        var rb = departures[3];
        Assert.Null(rb.DelayMinutes);
        Assert.Null(rb.Platform);
    }

    [Fact]
    public void Departures_also_read_the_plain_array_of_v5()
    {
        using var json = JsonDocument.Parse(Test.Data("departures.json"));
        var array = json.RootElement.GetProperty("departures").GetRawText();
        using var v5 = JsonDocument.Parse(array);

        Assert.Equal(4, TransportRestSource.ParseDepartures(v5.RootElement).Count);
    }

    [Fact]
    public void Journeys_skip_walks_count_changes_and_keep_the_arrival_delay()
    {
        using var json = JsonDocument.Parse(Test.Data("journeys.json"));

        var connections = TransportRestSource.ParseJourneys(json.RootElement);

        Assert.Equal(2, connections.Count);
        var first = connections[0];
        Assert.Equal("RE 7", first.Departure.Line);
        Assert.Equal(1, first.Changes);
        Assert.Equal(4, first.Departure.DelayMinutes);
        Assert.True(first.Departure.PlatformChanged);
        Assert.Equal(Test.At(17, 31), first.Arrival);
        Assert.Equal("T1", first.Departure.TripId);
        var second = connections[1];
        Assert.True(second.Departure.Cancelled);
        Assert.Equal(0, second.Changes);
        Assert.Null(second.ActualArrival);
    }

    [Fact]
    public void Station_search_takes_the_first_stop()
    {
        using var json = JsonDocument.Parse("""[{"type":"location","id":"1"},{"type":"stop","id":"8000107","name":"Freiburg(Breisgau) Hbf"}]""");

        Assert.Equal(new Station("8000107", "Freiburg(Breisgau) Hbf"), TransportRestSource.ParseStation(json.RootElement));
    }

    [Fact]
    public async Task Server_errors_become_readable_messages()
    {
        var handler = new FakeHttp();
        handler.Routes.Add(("/stops/", HttpStatusCode.ServiceUnavailable, ""));
        var source = new TransportRestSource(new HttpClient(handler), "https://example.org/");

        var error = await Assert.ThrowsAsync<BahnSourceException>(() =>
            source.DeparturesAsync(new Station("8000107", "Freiburg"), Test.Now, TimeSpan.FromMinutes(60), true, CancellationToken.None));

        Assert.Contains("503", error.Message);
        var url = handler.Requests.Single().RequestUri!.ToString();
        Assert.StartsWith("https://example.org/stops/8000107/departures?", url);
        Assert.Contains("bus=false", url);
        Assert.Contains("duration=60", url);
    }
}

public sealed class TimetablesTests
{
    public TimetablesTests() => Test.Setup();

    [Fact]
    public void Plan_and_changes_give_delay_new_platform_cancellation_and_direction()
    {
        var plan = TimetablesSource.ParsePlan(XDocument.Parse(Test.Data("plan.xml")));
        var changes = TimetablesSource.ParseChanges(XDocument.Parse(Test.Data("fchg.xml")));

        // Der endende RE (nur Ankunft) fährt hier nicht ab.
        Assert.Equal(4, plan.Count);
        var departures = plan.Select(s => changes.TryGetValue(s.Id, out var c) ? TimetablesSource.Apply(s, c) : s)
            .ToDictionary(s => s.Departure.Line, s => s);

        var re = departures["RE 7"].Departure;
        Assert.Equal(Test.At(16, 40), re.Planned);
        Assert.Equal(7, re.DelayMinutes);
        Assert.Equal("Offenburg", re.Direction);
        Assert.True(re.PlatformChanged);
        Assert.Equal("4", re.ShownPlatform);

        var ice = departures["ICE 104"].Departure;
        Assert.True(ice.Cancelled);
        Assert.Equal("Frankfurt(Main)Hbf", ice.Direction);

        // In den Änderungen ohne neue Zeit: pünktlich, mit Echtzeit.
        var s1 = departures["S1"].Departure;
        Assert.Equal(0, s1.DelayMinutes);
        Assert.False(s1.PlatformChanged);

        Assert.True(departures["Bus SEV"].Bus);
        Assert.Null(departures["Bus SEV"].Departure.DelayMinutes);
    }

    [Theory]
    [InlineData("RE", "7", "17012", "RE 7")]
    [InlineData("S", "S1", "87001", "S1")]
    [InlineData("ICE", null, "104", "ICE 104")]
    [InlineData("ICE", "", "104", "ICE 104")]
    [InlineData("", null, null, "Zug")]
    public void Line_names_read_like_on_the_platform(string category, string? line, string? number, string expected) =>
        Assert.Equal(expected, TimetablesSource.LineName(category, line, number));

    [Fact]
    public void Times_are_local_to_germany_also_in_summer()
    {
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 5, 0, TimeSpan.FromHours(2)), TimetablesSource.ParseTime("2607010805"));
        Assert.Equal(Test.At(16, 40), TimetablesSource.ParseTime("2610261640"));
        Assert.Null(TimetablesSource.ParseTime("kaputt"));
    }

    [Fact]
    public async Task Departures_merge_hours_with_changes_and_send_the_keys()
    {
        var handler = new FakeHttp();
        handler.Routes.Add(("/plan/8000107/261026/16", HttpStatusCode.OK, Test.Data("plan.xml")));
        handler.Routes.Add(("/fchg/8000107", HttpStatusCode.OK, Test.Data("fchg.xml")));
        var source = new TimetablesSource(new HttpClient(handler), "client", "key", new FakeTimeProvider(Test.Now));

        var departures = await source.DeparturesAsync(new Station("8000107", "Freiburg(Breisgau) Hbf"), Test.Now, TimeSpan.FromMinutes(60), true, CancellationToken.None);

        // Bus fehlt (nur Züge), Stunden ohne Plan (404) sind leer statt Fehler.
        Assert.Equal(["S1", "RE 7", "ICE 104"], departures.Select(d => d.Line));
        var first = handler.Requests[0];
        Assert.Equal("client", first.Headers.GetValues("DB-Client-Id").Single());
        Assert.Equal("key", first.Headers.GetValues("DB-Api-Key").Single());
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/plan/8000107/261026/15"));
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/plan/8000107/261026/17"));

        // Der Plan einer Stunde kommt aus dem Speicher, die Änderungen jedes Mal neu.
        var before = handler.Requests.Count;
        await source.DeparturesAsync(new Station("8000107", "Freiburg(Breisgau) Hbf"), Test.Now, TimeSpan.FromMinutes(60), true, CancellationToken.None);
        Assert.Equal(1, handler.Requests.Count - before);
    }

    [Fact]
    public async Task Commute_shows_direct_trains_that_stop_at_the_destination()
    {
        var handler = new FakeHttp();
        handler.Routes.Add(("/plan/8000107/261026/16", HttpStatusCode.OK, Test.Data("plan.xml")));
        handler.Routes.Add(("/fchg/8000107", HttpStatusCode.OK, Test.Data("fchg.xml")));
        var source = new TimetablesSource(new HttpClient(handler), "client", "key", new FakeTimeProvider(Test.Now));

        var connections = await source.ConnectionsAsync(new Station("8000107", "Freiburg(Breisgau) Hbf"), new Station("8000290", "Offenburg"), Test.Now, CancellationToken.None);

        Assert.Equal(["RE 7", "ICE 104"], connections.Select(c => c.Departure.Line));
    }

    [Fact]
    public async Task Wrong_keys_say_so()
    {
        var handler = new FakeHttp();
        handler.Routes.Add(("/station/", HttpStatusCode.Unauthorized, ""));
        var source = new TimetablesSource(new HttpClient(handler), "client", "falsch", new FakeTimeProvider(Test.Now));

        var error = await Assert.ThrowsAsync<BahnSourceException>(() => source.FindStationAsync("Freiburg", CancellationToken.None));

        Assert.Contains("API-Key", error.Message);
    }

    [Fact]
    public void Station_search_reads_eva_and_name()
    {
        Assert.Equal(new Station("8000107", "Freiburg(Breisgau) Hbf"), TimetablesSource.ParseStation(XDocument.Parse(Test.Data("stations.xml"))));
    }
}
