using System.Net;
using System.Reflection;

namespace Brim.Bahn;

public static class Sources
{
    /// <summary>Ein HttpClient für alle Anfragen; transport.rest bittet um einen erkennbaren User-Agent.</summary>
    public static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrimBahn/{version} (+https://github.com/wheatley2056/brim-db)");
        return http;
    }

    /// <summary>Mit Client-ID und API-Key die Timetables-API der DB, sonst transport.rest.</summary>
    public static IBahnSource Create(PluginSettings settings, HttpClient http, TimeProvider time) => settings.UsesDbApi
        ? new TimetablesSource(http, settings.DbClientId, settings.DbApiKey, time)
        : new TransportRestSource(http, settings.Server);
}
