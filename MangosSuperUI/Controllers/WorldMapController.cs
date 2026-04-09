using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;

namespace MangosSuperUI.Controllers;

public class WorldMapController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;
    private readonly HeightMapService _heightMap;

    private const int CUSTOM_RANGE_START = 900000;

    public WorldMapController(ConnectionFactory db, IWebHostEnvironment env, HeightMapService heightMap)
    {
        _db = db;
        _env = env;
        _heightMap = heightMap;
    }

    public IActionResult Index()
    {
        return View();
    }

    // ===================== TILE AVAILABILITY =====================

    /// <summary>
    /// GET /WorldMap/TileIndex?map=Azeroth
    /// Returns which mapXX_YY tiles exist on disk so the client knows what to request.
    /// </summary>
    [HttpGet]
    public IActionResult TileIndex(string map = "Azeroth")
    {
        map = SanitizeMapName(map);
        var tilesDir = Path.Combine(_env.WebRootPath, "minimap", map);

        if (!Directory.Exists(tilesDir))
            return Json(new { map, tiles = Array.Empty<int[]>() });

        var tiles = new List<int[]>();
        foreach (var file in Directory.GetFiles(tilesDir, "map*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "map27_30"
            var parts = name.Replace("map", "").Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], out int row) && int.TryParse(parts[1], out int col))
            {
                tiles.Add(new[] { row, col });
            }
        }

        return Json(new { map, tiles });
    }

    /// <summary>
    /// GET /WorldMap/AvailableMaps
    /// Returns list of map folders that have minimap tiles.
    /// </summary>
    [HttpGet]
    public IActionResult AvailableMaps()
    {
        var minimapRoot = Path.Combine(_env.WebRootPath, "minimap");
        if (!Directory.Exists(minimapRoot))
            return Json(new { maps = Array.Empty<object>() });

        var maps = new List<object>();
        foreach (var dir in Directory.GetDirectories(minimapRoot).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            var tileCount = Directory.GetFiles(dir, "map*.png").Length;
            if (tileCount > 0)
                maps.Add(new { name, tileCount });
        }

        return Json(new { maps });
    }

    // ===================== HEIGHT LOOKUP =====================

    /// <summary>
    /// GET /WorldMap/GetHeight?map=0&x=...&y=...
    /// Returns terrain Z height from pre-extracted .map files.
    /// </summary>
    [HttpGet]
    public IActionResult GetHeight(int map = 0, float x = 0, float y = 0)
    {
        if (!_heightMap.IsAvailable)
            return Json(new { z = (float?)null, error = "MapsDataPath not configured" });

        var z = _heightMap.GetHeight(map, x, y);
        return Json(new { z, available = true });
    }

    // ===================== GAMEOBJECT SPAWNS =====================

    /// <summary>
    /// GET /WorldMap/Spawns?map=0&minX=-5000&maxX=5000&minY=-5000&maxY=5000
    /// Returns gameobject spawns within a bounding box for overlay markers.
    /// map: 0=Eastern Kingdoms, 1=Kalimdor
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Spawns(int map = 0, float minX = -99999, float maxX = 99999,
                                             float minY = -99999, float maxY = 99999, bool customOnly = false)
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT g.guid, g.id AS entry, g.map, g.position_x AS x, g.position_y AS y, g.position_z AS z,
                   g.orientation, gt.name, gt.type
            FROM gameobject g
            JOIN gameobject_template gt ON gt.entry = g.id
                AND gt.patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = g.id)
            WHERE g.map = @Map
              AND g.position_x BETWEEN @MinX AND @MaxX
              AND g.position_y BETWEEN @MinY AND @MaxY";

        if (customOnly)
            sql += " AND g.id >= @CustomStart";

        sql += " ORDER BY g.id LIMIT 5000";

        var spawns = await conn.QueryAsync<dynamic>(sql, new
        {
            Map = map,
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY,
            CustomStart = CUSTOM_RANGE_START
        });

        return Json(new { spawns });
    }

    // ===================== CUSTOM OBJECT CATALOG =====================

    /// <summary>
    /// GET /WorldMap/Catalog
    /// Returns all custom gameobject templates (900000+) for the placement picker.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT entry, type, displayId, name
            FROM gameobject_template
            WHERE entry >= @Start
              AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
            ORDER BY entry";

        var objects = await conn.QueryAsync<dynamic>(sql, new { Start = CUSTOM_RANGE_START });

        return Json(new { objects });
    }

    // ===================== PLACE GAMEOBJECT =====================

    /// <summary>
    /// POST /WorldMap/PlaceObject
    /// Inserts a gameobject spawn directly into the gameobject table.
    /// Auto-resolves Z from heightmap if Z is 0 (or very close to it).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PlaceObject([FromBody] PlaceObjectRequest req)
    {
        if (req.Entry <= 0)
            return BadRequest(new { error = "Invalid entry" });

        using var conn = _db.Mangos();

        // Verify the template exists
        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM gameobject_template WHERE entry = @Entry",
            new { req.Entry });

        if (exists == 0)
            return BadRequest(new { error = $"Template entry {req.Entry} not found" });

        // Auto-resolve Z from heightmap if the client sent ~0
        float finalZ = req.Z;
        bool zResolved = false;
        if (Math.Abs(req.Z) < 0.1f && _heightMap.IsAvailable)
        {
            var terrainZ = _heightMap.GetHeight(req.Map, req.X, req.Y);
            if (terrainZ.HasValue)
            {
                finalZ = terrainZ.Value;
                zResolved = true;
            }
        }

        // Insert spawn — guid is auto_increment
        var sql = @"
            INSERT INTO gameobject (id, map, position_x, position_y, position_z,
                                    orientation, rotation0, rotation1, rotation2, rotation3,
                                    spawntimesecsmin, spawntimesecsmax, animprogress, state,
                                    spawn_flags, patch_min, patch_max)
            VALUES (@Entry, @Map, @X, @Y, @Z,
                    @Orientation, 0, 0, 0, 0,
                    180, 180, 100, 1,
                    0, 0, 10)";

        await conn.ExecuteAsync(sql, new
        {
            req.Entry,
            req.Map,
            X = req.X,
            Y = req.Y,
            Z = finalZ,
            Orientation = req.Orientation
        });

        var newGuid = await conn.ExecuteScalarAsync<int>("SELECT LAST_INSERT_ID()");

        return Json(new
        {
            success = true,
            guid = newGuid,
            z = finalZ,
            zResolved,
            message = $"Spawned entry {req.Entry} at ({req.X:F1}, {req.Y:F1}, {finalZ:F1}) — GUID {newGuid}"
                    + (zResolved ? " [Z from heightmap]" : "")
        });
    }

    /// <summary>
    /// DELETE /WorldMap/DeleteSpawn?guid=12345
    /// Removes a gameobject spawn.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> DeleteSpawn(int guid)
    {
        using var conn = _db.Mangos();
        var rows = await conn.ExecuteAsync("DELETE FROM gameobject WHERE guid = @Guid", new { Guid = guid });

        return Json(new { success = rows > 0, deleted = rows });
    }

    // ── Helpers ──

    private static string SanitizeMapName(string name)
    {
        // Prevent path traversal
        return Path.GetFileName(name ?? "Azeroth");
    }
}

public class PlaceObjectRequest
{
    public int Entry { get; set; }
    public int Map { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Orientation { get; set; }
}