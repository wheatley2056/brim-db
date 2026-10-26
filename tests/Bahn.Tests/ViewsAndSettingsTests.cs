using System.Globalization;
using System.Text.Json;
using Brim.Core;

namespace Brim.Bahn.Tests;

public sealed class ViewsTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public ViewsTests() => Test.Setup();

    [Theory]
    [InlineData(0, "jetzt")]
    [InlineData(4, "4 min")]
    [InlineData(59, "59 min")]
    [InlineData(75, "17:45")]
    public void Until_counts_minutes_then_shows_the_time(int minutes, string expected) =>
        Assert.Equal(expected, Views.Until(Test.Now.AddMinutes(minutes), Test.Now, German));

    [Fact]
    public void Delay_colors_follow_the_bahn_app()
    {
        Assert.Equal(BrimColors.Charge, Views.DelayColor(Test.Dep("RE 7", 16, 40, delay: 4)));
        Assert.Equal(BrimColors.Danger, Views.DelayColor(Test.Dep("RE 7", 16, 40, delay: 5)));
        Assert.Equal(BrimColors.Slate, Views.DelayColor(Test.Dep("RE 7", 16, 40, delay: null)));
        Assert.Equal(BrimColors.Danger, Views.DelayColor(Test.Dep("RE 7", 16, 40, cancelled: true)));
    }

    [Fact]
    public void Row_shows_plan_realtime_and_changed_platform()
    {
        var row = Test.Json(Views.DepartureRow(Test.Dep("RE 7", 16, 40, delay: 7, actualPlatform: "5"), true, Test.Now, German));

        Assert.Contains("\"16:40\"", row);
        Assert.Contains("\"16:47\"", row);
        Assert.Contains("Gl. 5", row);
        Assert.Contains("17 min", row);
        Assert.Contains("RE 7 nach Offenburg, 16:40, heute 16:47, Gleis 5 statt 3", row);
    }

    [Fact]
    public void Every_size_fits_the_limits_of_brim()
    {
        var board = new StationBoard(new Station("1", "Freiburg(Breisgau) Hbf"),
            Enumerable.Range(0, 10).Select(i => Test.Dep("RE 7", 16, 35 + i * 2)).ToList(), Test.Now, null);
        foreach (var size in Enum.GetValues<TileSize>())
        {
            var widget = Views.DeparturesWidget(board, 0, 2, size, Test.Now, German);
            Assert.True(Brim.Core.Widgets.WidgetValidator.Validate(widget) is null, $"{size}: {Brim.Core.Widgets.WidgetValidator.Validate(widget)}");
        }
    }

    [Fact]
    public void Empty_states_explain_what_to_do()
    {
        Assert.Contains("Bahnhöfe unter Einstellungen", Test.Json(Views.DeparturesWidget(null, 0, 0, TileSize.Large, Test.Now, German)));
        Assert.Contains("Pendelstrecke unter Einstellungen", Test.Json(Views.CommuteWidget(null, false, TileSize.Medium, Test.Now, German)));
        var loading = new StationBoard(new Station("1", "Freiburg"), [], null, null);
        Assert.Contains("Lädt", Test.Json(Views.DeparturesWidget(loading, 0, 1, TileSize.Medium, Test.Now, German)));
    }
}

public sealed class PluginSettingsTests
{
    private static Dictionary<string, JsonElement> Values(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Reads_text_numbers_and_switches()
    {
        var settings = PluginSettings.From(Values("""
            { "stations": "Freiburg Hbf, Offenburg; freiburg hbf", "walkMinutes": "7,5", "notch": "false", "notchMinutes": 20,
              "commuteFrom": " Freiburg ", "commuteTo": "Offenburg", "dbClientId": "a", "dbApiKey": "" }
            """));

        Assert.Equal(["Freiburg Hbf", "Offenburg"], settings.Stations);
        Assert.Equal(TimeSpan.FromMinutes(7.5), settings.Walk);
        Assert.False(settings.Notch);
        Assert.Equal(TimeSpan.FromMinutes(20), settings.NotchWindow);
        Assert.True(settings.HasCommute);
        Assert.Equal("Freiburg", settings.CommuteFrom);
        Assert.False(settings.UsesDbApi);
        Assert.Equal(TransportRestSource.DefaultServer, settings.Server);
    }

    [Fact]
    public void Defaults_without_values()
    {
        var settings = PluginSettings.From(Values("{}"));

        Assert.Empty(settings.Stations);
        Assert.True(settings.Notch);
        Assert.True(settings.Alerts);
        Assert.True(settings.TrainsOnly);
        Assert.Equal(TimeSpan.FromMinutes(30), settings.NotchWindow);
    }

    [Fact]
    public void Manifest_defaults_match_the_code()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Bahn", "plugin.json")));
        var values = manifest.RootElement.GetProperty("settings").EnumerateArray()
            .ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("default").Clone());

        var parsed = PluginSettings.From(values);
        Assert.Equal(new PluginSettings { Stations = parsed.Stations }, parsed);
    }
}
