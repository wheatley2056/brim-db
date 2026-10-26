// Bahn-Plugin für Brim: Abfahrten an Lieblingsbahnhöfen und eine Pendelstrecke mit Verspätungen, Ausfällen und
// Gleiswechseln, in der Notch und als Widgets auf „Jetzt“.
using Brim.Bahn;
using Brim.Sdk;

BrimPlugin brim;
try
{
    brim = await BrimPlugin.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Keine Verbindung zu Brim: {ex.Message}");
    return 1;
}

await using (brim)
{
    using var http = Sources.CreateHttp();
    var settings = PluginSettings.From(brim.Settings);
    var plugin = new BahnPlugin(new BrimSink(brim), s => Sources.Create(s, http, TimeProvider.System), TimeProvider.System, settings);
    Console.WriteLine($"Datenquelle: {plugin.Source.Name}, Bahnhöfe: {string.Join(", ", settings.Stations)}");

    brim.ActionInvoked += (action, value) => _ = plugin.HandleActionAsync(action);
    brim.SettingsChanged += values => _ = plugin.ApplySettingsAsync(PluginSettings.From(values));
    brim.WidgetsPlaced += placed => _ = plugin.SetPlacedWidgetsAsync(placed);
    _ = plugin.SetPlacedWidgetsAsync(brim.PlacedWidgets);

    using var stop = new CancellationTokenSource();
    var loop = plugin.RunAsync(stop.Token);
    await brim.RunAsync();

    // Brim gibt 2 Sekunden zum Beenden.
    stop.Cancel();
    await Task.WhenAny(loop, Task.Delay(500));
}
return 0;
