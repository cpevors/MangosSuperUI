using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text;
using System.IO.Compression;

namespace MangosSuperUI.Controllers;

public class DownloadsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly IWebHostEnvironment _env;

    private const int CUSTOM_RANGE_START = 900000;

    public DownloadsController(ConnectionFactory db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public async Task<IActionResult> Index()
    {
        // Regenerate Catalog.lua into the Placer addon folder on every page visit
        await RefreshPlacerCatalog();
        return View();
    }

    // ===================== ADDON LIST =====================

    /// <summary>
    /// GET /Downloads/AddonList — Returns metadata about all addons in wwwroot/addons/.
    /// Each subfolder is an addon. If a .zip with the same name exists alongside it, it's downloadable.
    /// </summary>
    [HttpGet]
    public IActionResult AddonList()
    {
        var addonsRoot = Path.Combine(_env.WebRootPath, "addons");
        if (!Directory.Exists(addonsRoot))
            return Json(new { addons = Array.Empty<object>() });

        var addons = new List<object>();

        foreach (var dir in Directory.GetDirectories(addonsRoot))
        {
            var folderName = Path.GetFileName(dir);

            // Read addon info from the .toc file
            string title = folderName;
            string notes = "";
            string version = "";
            string author = "";

            var tocPath = Path.Combine(dir, folderName + ".toc");
            if (System.IO.File.Exists(tocPath))
            {
                foreach (var line in System.IO.File.ReadLines(tocPath))
                {
                    if (line.StartsWith("## Title:")) title = line.Substring(9).Trim();
                    else if (line.StartsWith("## Notes:")) notes = line.Substring(9).Trim();
                    else if (line.StartsWith("## Version:")) version = line.Substring(11).Trim();
                    else if (line.StartsWith("## Author:")) author = line.Substring(10).Trim();
                }
            }

            var luaFiles = Directory.GetFiles(dir, "*.lua").Length;

            // Read README.md if present
            string readme = "";
            var readmePath = Path.Combine(dir, "README.md");
            if (System.IO.File.Exists(readmePath))
                readme = System.IO.File.ReadAllText(readmePath);

            addons.Add(new
            {
                folder = folderName,
                title,
                notes,
                version,
                author,
                luaFiles,
                readme
            });
        }

        return Json(new { addons });
    }

    // ===================== DOWNLOAD ADDON ZIP =====================

    /// <summary>
    /// GET /Downloads/Addon?name=MangosSuperUI_Placer — Generates a ZIP on-the-fly from wwwroot/addons/{name}/.
    /// </summary>
    [HttpGet]
    public IActionResult Addon(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("Addon name required");

        // Sanitize — prevent path traversal
        name = Path.GetFileName(name);

        var addonDir = Path.Combine(_env.WebRootPath, "addons", name);
        if (!Directory.Exists(addonDir))
            return NotFound($"Addon folder '{name}' not found");

        using var memoryStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(addonDir))
            {
                var entryName = name + "/" + Path.GetFileName(file);
                archive.CreateEntryFromFile(file, entryName, System.IO.Compression.CompressionLevel.Optimal);
            }
        }

        memoryStream.Position = 0;
        return File(memoryStream.ToArray(), "application/zip", name + ".zip");
    }

    // ===================== CATALOG REFRESH =====================

    /// <summary>
    /// Writes a fresh Catalog.lua into wwwroot/addons/MangosSuperUI_Placer/ from the DB.
    /// Called automatically when the Downloads page loads.
    /// </summary>
    private async Task RefreshPlacerCatalog()
    {
        var addonDir = Path.Combine(_env.WebRootPath, "addons", "MangosSuperUI_Placer");
        if (!Directory.Exists(addonDir)) return;

        try
        {
            var catalogLua = await BuildCatalogLua();
            var catalogPath = Path.Combine(addonDir, "Catalog.lua");
            await System.IO.File.WriteAllTextAsync(catalogPath, catalogLua, Encoding.UTF8);
        }
        catch
        {
            // Non-fatal — page still loads even if catalog write fails
        }
    }

    /// <summary>
    /// GET /Downloads/PlacerInfo — Returns metadata about the current Placer catalog.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> PlacerInfo()
    {
        using var conn = _db.Mangos();

        var objectCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM gameobject_template WHERE entry >= @Start AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)",
            new { Start = CUSTOM_RANGE_START });

        var spawnCount = 0;
        if (objectCount > 0)
        {
            spawnCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM gameobject WHERE id >= @Start",
                new { Start = CUSTOM_RANGE_START });
        }

        var typeCounts = await conn.QueryAsync<dynamic>(
            @"SELECT type, COUNT(*) AS cnt
              FROM gameobject_template
              WHERE entry >= @Start
                AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
              GROUP BY type ORDER BY cnt DESC",
            new { Start = CUSTOM_RANGE_START });

        return Json(new
        {
            objectCount,
            spawnCount,
            typeCounts
        });
    }

    // ── Catalog Builder ──────────────────────────────────────────────

    private async Task<string> BuildCatalogLua()
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT entry, type, displayId, name, data0, data1, data2, data3,
                   data4, data5, data6, data7, data8, data9, data10
            FROM gameobject_template
            WHERE entry >= @CustomStart
              AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
            ORDER BY entry";

        var objects = (await conn.QueryAsync<dynamic>(sql, new { CustomStart = CUSTOM_RANGE_START })).ToList();

        // Resolve spell names
        var spellIds = new HashSet<int>();
        foreach (var obj in objects)
        {
            int type = (int)(obj.type ?? 0);
            int d0 = (int)(obj.data0 ?? 0);
            int d1 = (int)(obj.data1 ?? 0);
            int d3 = (int)(obj.data3 ?? 0);
            if (type == 22 && d0 > 0) spellIds.Add(d0);
            if (type == 6 && d3 > 0) spellIds.Add(d3);
            if (type == 10 && (int)(obj.data10 ?? 0) > 0) spellIds.Add((int)obj.data10);
            if (type == 18 && d1 > 0) spellIds.Add(d1);
            if (type == 30 && (int)(obj.data2 ?? 0) > 0) spellIds.Add((int)obj.data2);
        }

        var spellNames = new Dictionary<int, string>();
        if (spellIds.Count > 0)
        {
            var spells = await conn.QueryAsync<dynamic>(
                "SELECT entry, name FROM spell_template WHERE entry IN @Ids AND build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)",
                new { Ids = spellIds.ToArray() });
            foreach (var sp in spells)
                spellNames[(int)sp.entry] = (string)sp.name;
        }

        var spawnCounts = new Dictionary<int, int>();
        if (objects.Count > 0)
        {
            var entries = objects.Select(o => (int)o.entry).ToArray();
            var counts = await conn.QueryAsync<dynamic>(
                "SELECT id, COUNT(*) AS cnt FROM gameobject WHERE id IN @Ids GROUP BY id",
                new { Ids = entries });
            foreach (var c in counts)
                spawnCounts[(int)c.id] = (int)c.cnt;
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- MangosSuperUI_Placer Catalog");
        sb.AppendLine($"-- Auto-generated by MangosSuperUI on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- {objects.Count} custom game object(s)");
        sb.AppendLine();
        sb.AppendLine("MSUI_CATALOG = {");

        foreach (var obj in objects)
        {
            int entry = (int)obj.entry;
            int type = (int)(obj.type ?? 0);
            string name = LuaEscape((string)(obj.name ?? "Unknown"));
            int displayId = (int)(obj.displayId ?? 0);
            string desc = BuildCatalogDesc(obj, type, spellNames);
            int spawns = spawnCounts.ContainsKey(entry) ? spawnCounts[entry] : 0;

            sb.AppendLine($"    [{entry}] = {{ name = \"{name}\", type = {type}, displayId = {displayId}, spawns = {spawns}, desc = \"{LuaEscape(desc)}\" }},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("MSUI_TYPE_NAMES = {");
        sb.AppendLine("    [0] = \"Door\", [1] = \"Button\", [2] = \"Quest Giver\", [3] = \"Chest\",");
        sb.AppendLine("    [5] = \"Generic\", [6] = \"Trap\", [7] = \"Chair\", [8] = \"Spell Focus\",");
        sb.AppendLine("    [9] = \"Text\", [10] = \"Goober\", [11] = \"Transport\", [13] = \"Camera\",");
        sb.AppendLine("    [15] = \"MO Transport\", [17] = \"Fishing Node\", [18] = \"Ritual\",");
        sb.AppendLine("    [19] = \"Mailbox\", [20] = \"Auction House\", [22] = \"Spell Caster\",");
        sb.AppendLine("    [23] = \"Meeting Stone\", [24] = \"Flag Stand\", [25] = \"Fishing Hole\",");
        sb.AppendLine("    [26] = \"Flag Drop\", [29] = \"Capture Point\", [30] = \"Aura Generator\",");
        sb.AppendLine("    [31] = \"Dungeon Difficulty\",");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string BuildCatalogDesc(dynamic obj, int type, Dictionary<int, string> spellNames)
    {
        int d0 = (int)(obj.data0 ?? 0);
        int d1 = (int)(obj.data1 ?? 0);
        int d3 = (int)(obj.data3 ?? 0);

        return type switch
        {
            22 => d0 > 0
                ? $"Casts {(spellNames.ContainsKey(d0) ? spellNames[d0] : $"Spell #{d0}")}"
                  + (d1 == -1 ? " (unlimited)" : d1 <= 1 ? " (single use)" : $" ({d1} charges)")
                : "Spell Caster",
            6 => d3 > 0 ? $"Trap: {(spellNames.ContainsKey(d3) ? spellNames[d3] : $"Spell #{d3}")}" : "Trap",
            3 => d1 > 0 ? $"Chest (loot #{d1})" : "Chest (no loot)",
            10 => d1 > 0 ? $"Goober (quest #{d1})" : "Clickable object",
            30 => (int)(obj.data2 ?? 0) > 0 && spellNames.ContainsKey((int)obj.data2)
                ? $"Aura: {spellNames[(int)obj.data2]}" : "Aura Generator",
            5 => "Decoration",
            0 => "Door",
            1 => "Button",
            2 => "Quest Giver",
            7 => "Chair",
            8 => "Spell Focus",
            9 => "Text",
            _ => $"Type {type}"
        };
    }

    private static string LuaEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }

}