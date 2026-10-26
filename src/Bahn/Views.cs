using System.Globalization;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;

namespace Brim.Bahn;

/// <summary>Alle Inhalte des Plugins als Widget-Bäume. Reine Funktionen, damit Tests sie prüfen können.</summary>
public static class Views
{
    /// <summary>Segoe Fluent Icons „Train“.</summary>
    public static readonly string TrainGlyph = "";

    public const string Accent = "#FF6B6B";

    public const string ActivityId = "next";
    public const string NextStationAction = "station.next";
    public const string RefreshAction = "refresh";

    /// <summary>Ab so viel Verspätung (Minuten) wird die Zeit rot, wie in der App der Bahn.</summary>
    public const int LateMinutes = 5;

    /// <summary>Zeitzone für Uhrzeiten; Tests setzen sie fest.</summary>
    public static TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    // ───────── Texte ─────────

    /// <summary>„15:42“.</summary>
    public static string Clock(DateTimeOffset time, CultureInfo culture) => TimeZoneInfo.ConvertTime(time, Zone).ToString("HH:mm", culture);

    /// <summary>„jetzt“, „4 min“, ab einer Stunde die Uhrzeit.</summary>
    public static string Until(DateTimeOffset time, DateTimeOffset now, CultureInfo culture)
    {
        var minutes = (int)Math.Floor((time - now).TotalMinutes);
        return minutes switch
        {
            <= 0 => "jetzt",
            < 60 => minutes.ToString(culture) + " min",
            _ => Clock(time, culture),
        };
    }

    /// <summary>„RE  7“ → „RE 7“.</summary>
    public static string CompactLine(string line) =>
        string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string Platform(string? platform) => platform is { Length: > 0 } p ? "Gl. " + p : "";

    /// <summary>Grün bis 4 Minuten Verspätung, ab <see cref="LateMinutes"/> rot; ohne Echtzeit grau.</summary>
    public static string DelayColor(Departure d) => d.Cancelled ? BrimColors.Danger : d.DelayMinutes switch
    {
        null => BrimColors.Slate,
        < LateMinutes => BrimColors.Charge,
        _ => BrimColors.Danger,
    };

    /// <summary>Für die Sprachausgabe: „RE 7 nach Offenburg, 15:42, heute 15:47, Gleis 5 statt 3“.</summary>
    public static string Describe(Departure d, CultureInfo culture)
    {
        var text = $"{d.Line} nach {d.Direction}, {Clock(d.Planned, culture)}";
        if (d.Cancelled)
        {
            return text + ", fällt aus";
        }
        if (d.DelayMinutes is > 0)
        {
            text += $", heute {Clock(d.When, culture)}";
        }
        if (d.PlatformChanged)
        {
            text += $", Gleis {d.Platform} statt {d.PlannedPlatform}";
        }
        else if (d.ShownPlatform is { Length: > 0 } platform)
        {
            text += $", Gleis {platform}";
        }
        return text;
    }

    // ───────── Notch ─────────

    /// <summary>
    /// Die Aktivität „next“: kompakt Linie und Minuten bis zur Abfahrt, rot bei Ausfall oder Verspätung, gelb bei
    /// Gleiswechsel. Fährt der Zug erst später, bleibt sie im Hintergrund und ist nur aufgeklappt zu sehen.
    /// </summary>
    public static ActivityParams Activity(Departure next, string? station, bool background, Widget expanded, DateTimeOffset now, CultureInfo culture)
    {
        var color = next.Cancelled || next.DelayMinutes >= LateMinutes ? BrimColors.Danger
            : next.PlatformChanged ? BrimColors.Warning
            : BrimColors.Fog;
        var trailing = next.Cancelled ? $"{next.Line} fällt aus" : $"{next.Line} · {Until(next.When, now, culture)}";
        return new ActivityParams
        {
            Id = ActivityId,
            Priority = ActivityPriority.Ongoing,
            Leading = new IconWidget(TrainGlyph) { Size = 14, Color = next.Cancelled ? BrimColors.Danger : Accent },
            Trailing = new TextWidget(trailing)
            {
                Style = TextStyle.Caption,
                FontSize = 12,
                Color = color,
                AccessibleName = (station is null ? "" : station + ": ") + Describe(next, culture),
            },
            Bead = new IconWidget(TrainGlyph) { Size = 8, Color = Accent },
            Expanded = expanded,
            Accent = Accent,
            Rank = 4,
            Background = background,
        };
    }

    /// <summary>Aufgeklappt: Pendelstrecke und erster Bahnhof untereinander.</summary>
    public static Widget Expanded(StationBoard? board, CommuteBoard? commute, DateTimeOffset now, CultureInfo culture)
    {
        var children = new List<Widget>();
        if (commute is not null)
        {
            children.Add(Header(TrainGlyph, $"{commute.From.Name} → {commute.To.Name}", commute.Error is null ? null : "offline", BrimColors.Warning));
            children.AddRange(commute.Connections.Take(4).Select(c => ConnectionRow(c, true, culture)));
            if (commute.Connections.Count == 0)
            {
                children.Add(Note(EmptyText(commute.Error, commute.UpdatedAt, "Keine Verbindung in den nächsten zwei Stunden")));
            }
        }
        if (board is not null)
        {
            if (children.Count > 0)
            {
                children.Add(new SpacerWidget { Size = 6 });
            }
            children.Add(Header(TrainGlyph, board.Station.Name, board.Error is null ? null : "offline", BrimColors.Warning));
            children.AddRange(board.Departures.Take(commute is null ? 7 : 4).Select(d => DepartureRow(d, true, now, culture)));
            if (board.Departures.Count == 0)
            {
                children.Add(Note(EmptyText(board.Error, board.UpdatedAt, "Keine Abfahrten in den nächsten 90 Minuten")));
            }
        }
        return new StackWidget(children)
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 4,
            Align = CrossAlign.Stretch,
        };
    }

    /// <summary>Kurze Meldung bei Ausfall, Gleiswechsel oder mehr Verspätung: „RE 7 15:42 nach Offenburg: fällt aus“.</summary>
    public static HudParams AlertHud(Departure next, string change, CultureInfo culture)
    {
        var text = $"{next.Line} {Clock(next.Planned, culture)} nach {next.Direction}: {change}";
        return new HudParams
        {
            Id = "alert",
            Content = new StackWidget(
                new IconWidget(TrainGlyph) { Size = 16, Color = next.Cancelled ? BrimColors.Danger : BrimColors.Warning },
                new TextWidget(text) { Style = TextStyle.Body, Grow = 1 })
            {
                Spacing = 12,
                Padding = (18, 0, 18, 0),
            },
            DurationMs = 8000,
            Announcement = text,
        };
    }

    // ───────── Widget „Abfahrten“ ─────────

    public static Widget DeparturesWidget(StationBoard? board, int selected, int stationCount, TileSize size, DateTimeOffset now, CultureInfo culture)
    {
        if (board is null)
        {
            return TileCard.Empty(TrainGlyph, size == TileSize.Small ? "Kein Bahnhof" : "Bahnhöfe unter Einstellungen → Plugins → Bahn eintragen");
        }
        // Bei mehreren Bahnhöfen blättert ein Klick auf die Karte zum nächsten.
        string? trailing = board.Error is not null ? "offline" : stationCount > 1 && size != TileSize.Small ? $"{selected + 1}/{stationCount}" : null;
        var header = Header(TrainGlyph, board.Station.Name, trailing, board.Error is null ? null : BrimColors.Warning);
        var action = stationCount > 1 ? NextStationAction : null;
        if (board.Departures.Count == 0)
        {
            return Card(header, Note(EmptyText(board.Error, board.UpdatedAt, "Keine Abfahrten")), action);
        }
        if (size == TileSize.Small)
        {
            return Card(header, SmallBody(board.Departures[0], now, culture), action);
        }
        var wide = size is TileSize.Wide or TileSize.Full;
        var rows = size is TileSize.Large or TileSize.Full ? 6 : 2;
        return Card(header, new StackWidget(board.Departures.Take(rows).Select(d => DepartureRow(d, wide, now, culture)).ToList())
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 3,
            Align = CrossAlign.Stretch,
        }, action);
    }

    /// <summary>Kleine Kachel: „RE 7 · 4 min“, darunter Gleis und Zeit.</summary>
    private static Widget SmallBody(Departure d, DateTimeOffset now, CultureInfo culture) => new StackWidget(
        new TextWidget(d.Cancelled ? $"{d.Line} fällt aus" : $"{d.Line} · {Until(d.When, now, culture)}")
        {
            Style = TextStyle.Body,
            FontSize = 13,
            Color = d.Cancelled ? BrimColors.Danger : null,
        },
        new StackWidget(
            new TextWidget(Clock(d.When, culture)) { Style = TextStyle.MonoCaption, Color = DelayColor(d) },
            new TextWidget(Platform(d.ShownPlatform)) { Style = TextStyle.Caption, Color = d.PlatformChanged ? BrimColors.Warning : null })
        {
            Spacing = 6,
        })
    {
        Orientation = StackOrientation.Vertical,
        Spacing = 0,
        Align = CrossAlign.Start,
        AccessibleName = Describe(d, culture),
    };

    /// <summary>Eine Zeile: Plan, Echtzeit, Linie, Ziel, (breit: Minuten), Gleis.</summary>
    public static Widget DepartureRow(Departure d, bool wide, DateTimeOffset now, CultureInfo culture)
    {
        var children = new List<Widget>
        {
            new TextWidget(Clock(d.Planned, culture)) { Style = TextStyle.Mono, FontSize = 12, FixedWidth = 38 },
            new TextWidget(d.Actual is { } actual && !d.Cancelled ? Clock(actual, culture) : "") { Style = TextStyle.Mono, FontSize = 12, Color = DelayColor(d), FixedWidth = 38 },
            new TextWidget(d.Line) { Style = TextStyle.Body, FontSize = 12, FixedWidth = wide ? 64 : 46 },
            new TextWidget(d.Cancelled ? "fällt aus · " + d.Direction : d.Direction)
            {
                Style = TextStyle.Secondary,
                FontSize = 12,
                Color = d.Cancelled ? BrimColors.Danger : null,
                Grow = 1,
            },
        };
        if (wide)
        {
            children.Add(new TextWidget(d.Cancelled ? "" : Until(d.When, now, culture)) { Style = TextStyle.Caption, FixedWidth = 44 });
        }
        children.Add(new TextWidget(Platform(d.ShownPlatform))
        {
            Style = TextStyle.Caption,
            Color = d.PlatformChanged ? BrimColors.Warning : null,
            FixedWidth = 38,
        });
        return new StackWidget(children) { Spacing = 6, AccessibleName = Describe(d, culture) };
    }

    // ───────── Widget „Pendelstrecke“ ─────────

    public static Widget CommuteWidget(CommuteBoard? commute, bool configured, TileSize size, DateTimeOffset now, CultureInfo culture)
    {
        if (!configured)
        {
            return TileCard.Empty(TrainGlyph, "Pendelstrecke unter Einstellungen → Plugins → Bahn eintragen");
        }
        if (commute is null)
        {
            return TileCard.Empty(TrainGlyph, "Lädt …");
        }
        var header = Header(TrainGlyph, $"{commute.From.Name} → {commute.To.Name}", commute.Error is null ? null : "offline", BrimColors.Warning);
        if (commute.Connections.Count == 0)
        {
            return Card(header, Note(EmptyText(commute.Error, commute.UpdatedAt, "Keine Verbindung in den nächsten zwei Stunden")), null);
        }
        var rows = size is TileSize.Large or TileSize.Full ? 6 : 2;
        var wide = size is TileSize.Wide or TileSize.Full;
        return Card(header, new StackWidget(commute.Connections.Take(rows).Select(c => ConnectionRow(c, wide, culture)).ToList())
        {
            Orientation = StackOrientation.Vertical,
            Spacing = 3,
            Align = CrossAlign.Stretch,
        }, null);
    }

    /// <summary>Eine Verbindung: Abfahrt (Plan, Echtzeit), Linie, Ankunft und Umstiege, Gleis.</summary>
    public static Widget ConnectionRow(Connection c, bool wide, CultureInfo culture)
    {
        var d = c.Departure;
        var changes = c.Changes switch { 0 => "direkt", 1 => "1 Umstieg", var n => $"{n} Umstiege" };
        var arrival = c.Arrival is { } at ? $"an {Clock(at, culture)}" : "";
        var middle = d.Cancelled ? "fällt aus"
            : wide ? string.Join(" · ", new[] { arrival, changes, d.Direction.Length > 0 && c.Changes == 0 ? "nach " + d.Direction : "" }.Where(s => s.Length > 0))
            : arrival.Length > 0 ? $"{arrival} · {changes}" : changes;
        var arrivalLate = c.ActualArrival is { } actualArrival && c.PlannedArrival is { } plannedArrival
            && (actualArrival - plannedArrival).TotalMinutes >= LateMinutes;
        return new StackWidget(
            new TextWidget(Clock(d.Planned, culture)) { Style = TextStyle.Mono, FontSize = 12, FixedWidth = 38 },
            new TextWidget(d.Actual is { } actual && !d.Cancelled ? Clock(actual, culture) : "") { Style = TextStyle.Mono, FontSize = 12, Color = DelayColor(d), FixedWidth = 38 },
            new TextWidget(d.Line) { Style = TextStyle.Body, FontSize = 12, FixedWidth = wide ? 64 : 46 },
            new TextWidget(middle)
            {
                Style = TextStyle.Secondary,
                FontSize = 12,
                Color = d.Cancelled || arrivalLate ? BrimColors.Danger : null,
                Grow = 1,
            },
            new TextWidget(Platform(d.ShownPlatform)) { Style = TextStyle.Caption, Color = d.PlatformChanged ? BrimColors.Warning : null, FixedWidth = 38 })
        {
            Spacing = 6,
            AccessibleName = $"{Describe(d, culture)}, {middle}",
        };
    }

    // ───────── Bausteine ─────────

    /// <summary>Kopfzeile wie <see cref="TileCard.Header"/>, rechts optional ein farbiger Hinweis.</summary>
    public static StackWidget Header(string glyph, string title, string? trailing, string? trailingColor)
    {
        var children = new List<Widget>
        {
            new IconWidget(glyph) { Size = 12, Color = Accent },
            new TextWidget(title) { Style = TextStyle.Caption, Grow = 1 },
        };
        if (trailing is not null)
        {
            children.Add(new TextWidget(trailing) { Style = TextStyle.Caption, Color = trailingColor });
        }
        return new StackWidget(children) { Spacing = 6 };
    }

    private static Widget Card(StackWidget header, Widget body, string? actionId) =>
        TileCard.Card(header, body) with { ActionId = actionId };

    private static Widget Note(string text) => new TextWidget(text) { Style = TextStyle.Secondary, FontSize = 12, Wrap = true };

    private static string EmptyText(string? error, DateTimeOffset? updatedAt, string none) =>
        error is not null ? error : updatedAt is null ? "Lädt …" : none;
}
