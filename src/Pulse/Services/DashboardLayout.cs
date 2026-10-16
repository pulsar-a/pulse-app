using System.Text.Json.Serialization;

namespace Pulse.Services;

/// <summary>What a dashboard tile shows.</summary>
public enum TileKind
{
    Processor,
    Graphics,
    Memory,
    Power,
    Cores,
    Cooling,
    Fans,
    Drives,
    Network,

    /// <summary>The busiest programs by CPU, GPU and memory (step 10).</summary>
    Processes,

    /// <summary>Sensors the user pinned: one sensor in large type, or a few as a group.</summary>
    Pinned,
}

/// <summary>How wide a tile is, in columns of the 12-column dashboard grid.</summary>
public enum TileSize
{
    Small,   // 3 columns: four in a row
    Medium,  // 4 columns: three in a row
    Large,   // 6 columns: two in a row
    Wide,    // 8 columns
    Full,    // 12 columns
}

/// <summary>The dashboard's tiles in order. Null means the default layout, so it can evolve with Pulse.</summary>
public sealed class DashboardSettings
{
    public List<TileSettings>? Tiles { get; set; }
}

/// <summary>
/// One tile as saved in settings.json. Kind and size are stored as text and read leniently, so a
/// settings file from a newer Pulse (with tile kinds this one doesn't know) never breaks loading.
/// </summary>
public sealed class TileSettings
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Size { get; set; } = "";
    public bool Hidden { get; set; }

    /// <summary>Pinned tiles: a custom title for a group.</summary>
    public string? Title { get; set; }

    /// <summary>Pinned tiles: the sensors shown, in order.</summary>
    public List<string>? Sensors { get; set; }

    [JsonIgnore]
    public TileKind? KindValue => Enum.TryParse<TileKind>(Kind, out var kind) ? kind : null;

    [JsonIgnore]
    public TileSize SizeValue => Enum.TryParse<TileSize>(Size, out var size) ? size : DashboardLayout.DefaultSize(KindValue ?? TileKind.Pinned);

    public TileSettings Clone() => new()
    {
        Id = Id, Kind = Kind, Size = Size, Hidden = Hidden, Title = Title,
        Sensors = Sensors is null ? null : [.. Sensors],
    };
}

/// <summary>
/// The rules of the dashboard layout: the default tiles, allowed sizes, and every edit (move, hide, resize,
/// pin). Edits work on the list stored in settings; the dashboard re-syncs from it. Pure logic, easy to test.
/// </summary>
public static class DashboardLayout
{
    public const int Columns = 12;
    public const string PinnedPrefix = "pinned-";

    private static readonly (TileKind Kind, TileSize Size)[] Defaults =
    [
        (TileKind.Processor, TileSize.Small),
        (TileKind.Graphics, TileSize.Small),
        (TileKind.Memory, TileSize.Small),
        (TileKind.Power, TileSize.Small),
        (TileKind.Cores, TileSize.Wide),
        (TileKind.Cooling, TileSize.Medium),
        (TileKind.Fans, TileSize.Medium),
        (TileKind.Drives, TileSize.Medium),
        (TileKind.Network, TileSize.Medium),
        (TileKind.Processes, TileSize.Full),
    ];

    public static int Span(TileSize size) => size switch
    {
        TileSize.Small => 3,
        TileSize.Medium => 4,
        TileSize.Large => 6,
        TileSize.Wide => 8,
        _ => 12,
    };

    public static TileSize DefaultSize(TileKind kind)
    {
        foreach (var (k, size) in Defaults)
        {
            if (k == kind) return size;
        }
        return TileSize.Small;
    }

    /// <summary>The sizes that make sense for each kind: a ring gauge can't be squeezed below a quarter.</summary>
    public static IReadOnlyList<TileSize> AllowedSizes(TileKind kind) => kind switch
    {
        TileKind.Cores => [TileSize.Large, TileSize.Wide, TileSize.Full],
        TileKind.Pinned => [TileSize.Small, TileSize.Medium, TileSize.Large, TileSize.Wide],
        TileKind.Drives => [TileSize.Medium, TileSize.Large, TileSize.Wide],
        TileKind.Processes => [TileSize.Medium, TileSize.Large, TileSize.Wide, TileSize.Full],
        _ => [TileSize.Small, TileSize.Medium, TileSize.Large],
    };

    /// <summary>The built-in cards' ids are their kind, in lower case ("processor", "fans").</summary>
    public static string BuiltInId(TileKind kind) => kind.ToString().ToLowerInvariant();

    public static List<TileSettings> Default() =>
        Defaults.Select(d => new TileSettings { Id = BuiltInId(d.Kind), Kind = d.Kind.ToString(), Size = d.Size.ToString() }).ToList();

    /// <summary>The saved layout, or the default when there's none.</summary>
    public static List<TileSettings> Current(DashboardSettings settings) => settings.Tiles ?? Default();

    /// <summary>
    /// Repairs a loaded layout: drops tiles of unknown kinds, duplicates and empty pinned tiles, fixes sizes,
    /// and adds built-in cards that are missing (a card added in a newer version shows up at the end).
    /// Returns true if anything changed.
    /// </summary>
    public static bool Normalize(List<TileSettings> tiles)
    {
        var changed = false;
        var seen = new HashSet<string>();
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var keep = tile is not null && tile.KindValue is { } kind && !string.IsNullOrEmpty(tile.Id) && seen.Add(tile.Id)
                       && (kind == TileKind.Pinned ? tile.Sensors is { Count: > 0 } : tile.Id == BuiltInId(kind));
            if (!keep)
            {
                tiles.RemoveAt(i--);
                changed = true;
                continue;
            }

            if (!AllowedSizes(tile!.KindValue!.Value).Contains(tile.SizeValue) || tile.Size != tile.SizeValue.ToString())
            {
                tile.Size = (AllowedSizes(tile.KindValue.Value).Contains(tile.SizeValue) ? tile.SizeValue : DefaultSize(tile.KindValue.Value)).ToString();
                changed = true;
            }
        }

        foreach (var (kind, size) in Defaults)
        {
            if (seen.Contains(BuiltInId(kind))) continue;
            tiles.Add(new TileSettings { Id = BuiltInId(kind), Kind = kind.ToString(), Size = size.ToString() });
            changed = true;
        }
        return changed;
    }

    // ---- Edits --------------------------------------------------------------------------------

    /// <summary>Moves a tile to just before <paramref name="beforeId"/>, or to the end when that's null.</summary>
    public static bool Move(List<TileSettings> tiles, string id, string? beforeId)
    {
        var from = tiles.FindIndex(t => t.Id == id);
        if (id == beforeId || from < 0)
            return false;
        var tile = tiles[from];
        tiles.RemoveAt(from);
        var to = beforeId is null ? -1 : tiles.FindIndex(t => t.Id == beforeId);
        if (to < 0) to = tiles.Count;
        tiles.Insert(to, tile);
        return to != from;
    }

    public static bool SetHidden(List<TileSettings> tiles, string id, bool hidden)
    {
        if (tiles.Find(t => t.Id == id) is not { } tile || tile.Hidden == hidden)
            return false;
        tile.Hidden = hidden;
        return true;
    }

    public static bool Resize(List<TileSettings> tiles, string id, TileSize size)
    {
        if (tiles.Find(t => t.Id == id) is not { KindValue: { } kind } tile || !AllowedSizes(kind).Contains(size) || tile.SizeValue == size)
            return false;
        tile.Size = size.ToString();
        return true;
    }

    /// <summary>
    /// Pins a sensor: into an existing pinned tile (making it a group), or as a new tile at the end.
    /// A sensor already pinned somewhere isn't added twice. Returns the tile it ended up in.
    /// </summary>
    public static string Pin(List<TileSettings> tiles, string sensorId, string? intoTileId = null)
    {
        if (intoTileId is null && PinnedTileOf(tiles, sensorId) is { } existing)
        {
            existing.Hidden = false;
            return existing.Id;
        }

        if (intoTileId is not null && tiles.Find(t => t.Id == intoTileId && t.KindValue == TileKind.Pinned) is { } target)
        {
            target.Sensors ??= [];
            if (!target.Sensors.Contains(sensorId))
                target.Sensors.Add(sensorId);
            // A group needs room for its rows.
            if (target.SizeValue == TileSize.Small && target.Sensors.Count > 1)
                target.Size = TileSize.Medium.ToString();
            target.Hidden = false;
            return target.Id;
        }

        var number = 1;
        while (tiles.Any(t => t.Id == $"{PinnedPrefix}{number}")) number++;
        var tile = new TileSettings
        {
            Id = $"{PinnedPrefix}{number}",
            Kind = TileKind.Pinned.ToString(),
            Size = TileSize.Small.ToString(),
            Sensors = [sensorId],
        };
        tiles.Add(tile);
        return tile.Id;
    }

    /// <summary>Takes a sensor off the dashboard; a pinned tile left empty is removed.</summary>
    public static bool Unpin(List<TileSettings> tiles, string sensorId, string? fromTileId = null)
    {
        var changed = false;
        foreach (var tile in tiles.Where(t => t.KindValue == TileKind.Pinned && (fromTileId is null || t.Id == fromTileId)).ToList())
        {
            if (tile.Sensors?.Remove(sensorId) != true) continue;
            changed = true;
            if (tile.Sensors.Count == 0)
                tiles.Remove(tile);
        }
        return changed;
    }

    /// <summary>Removes a pinned tile; built-in cards can only be hidden.</summary>
    public static bool Remove(List<TileSettings> tiles, string id) =>
        tiles.RemoveAll(t => t.Id == id && t.KindValue == TileKind.Pinned) > 0;

    public static TileSettings? PinnedTileOf(IEnumerable<TileSettings> tiles, string sensorId) =>
        tiles.FirstOrDefault(t => t.KindValue == TileKind.Pinned && t.Sensors?.Contains(sensorId) == true);
}
