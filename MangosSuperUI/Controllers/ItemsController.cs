using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class ItemsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;

    // Custom items start at this entry ID
    private const int CUSTOM_RANGE_START = 900000;

    // Columns we read/write for the full item row.
    // Matches item_template snake_case column names exactly.
    private static readonly string[] EDITABLE_COLUMNS = new[]
    {
        // Identity & display
        "name", "description", "class", "subclass", "quality", "display_id",
        "inventory_type", "flags",
        // Requirements
        "required_level", "item_level", "required_skill", "required_skill_rank",
        "required_spell", "required_honor_rank", "required_city_rank",
        "required_reputation_faction", "required_reputation_rank",
        "allowable_class", "allowable_race",
        // Economics & stacking
        "buy_price", "sell_price", "buy_count", "bonding", "stackable", "max_count",
        // Armor & resistances
        "armor", "block", "holy_res", "fire_res", "nature_res", "frost_res", "shadow_res", "arcane_res",
        // Weapon
        "dmg_min1", "dmg_max1", "dmg_type1", "dmg_min2", "dmg_max2", "dmg_type2",
        "dmg_min3", "dmg_max3", "dmg_type3", "dmg_min4", "dmg_max4", "dmg_type4",
        "dmg_min5", "dmg_max5", "dmg_type5",
        "delay", "range_mod", "ammo_type",
        // Stats
        "stat_type1", "stat_value1", "stat_type2", "stat_value2",
        "stat_type3", "stat_value3", "stat_type4", "stat_value4",
        "stat_type5", "stat_value5", "stat_type6", "stat_value6",
        "stat_type7", "stat_value7", "stat_type8", "stat_value8",
        "stat_type9", "stat_value9", "stat_type10", "stat_value10",
        // Spells (all 5 slots, all fields)
        "spellid_1", "spelltrigger_1", "spellcooldown_1", "spellcharges_1", "spellppmrate_1", "spellcategory_1", "spellcategorycooldown_1",
        "spellid_2", "spelltrigger_2", "spellcooldown_2", "spellcharges_2", "spellppmrate_2", "spellcategory_2", "spellcategorycooldown_2",
        "spellid_3", "spelltrigger_3", "spellcooldown_3", "spellcharges_3", "spellppmrate_3", "spellcategory_3", "spellcategorycooldown_3",
        "spellid_4", "spelltrigger_4", "spellcooldown_4", "spellcharges_4", "spellppmrate_4", "spellcategory_4", "spellcategorycooldown_4",
        "spellid_5", "spelltrigger_5", "spellcooldown_5", "spellcharges_5", "spellppmrate_5", "spellcategory_5", "spellcategorycooldown_5",
        // Physical properties
        "material", "sheath", "max_durability", "container_slots",
        // Misc
        "random_property", "set_id", "disenchant_id",
        "page_text", "page_language", "page_material",
        "start_quest", "lock_id",
        "area_bound", "map_bound", "duration", "bag_family",
        "food_type", "min_money_loot", "max_money_loot", "wrapped_gift",
        "extra_flags", "other_team_entry"
    };

    public ItemsController(ConnectionFactory db, DbcService dbc, AuditService audit, IWebHostEnvironment env)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
        _env = env;
    }

    public IActionResult Index() => View();

    // ===================== SEARCH (existing, unchanged) =====================

    /// <summary>
    /// GET /Items/Search?q=sword&classFilter=2&qualityFilter=4&page=1&pageSize=50
    /// Server-side search with pagination.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search(string? q, int? classFilter, int? subclassFilter,
        int? qualityFilter, int? inventoryTypeFilter, int page = 1, int pageSize = 50)
    {
        using var conn = _db.Mangos();

        var where = "WHERE patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = item_template.entry)";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(q))
        {
            if (uint.TryParse(q.Trim(), out var entryId))
            {
                where += " AND entry = @EntryId";
                parameters.Add("EntryId", entryId);
            }
            else
            {
                where += " AND name LIKE @Search";
                parameters.Add("Search", $"%{q.Trim()}%");
            }
        }

        if (classFilter.HasValue)
        {
            where += " AND class = @Class";
            parameters.Add("Class", classFilter.Value);
        }

        if (subclassFilter.HasValue)
        {
            where += " AND subclass = @Subclass";
            parameters.Add("Subclass", subclassFilter.Value);
        }

        if (qualityFilter.HasValue)
        {
            where += " AND quality = @Quality";
            parameters.Add("Quality", qualityFilter.Value);
        }

        if (inventoryTypeFilter.HasValue)
        {
            where += " AND inventory_type = @InvType";
            parameters.Add("InvType", inventoryTypeFilter.Value);
        }

        var countSql = $"SELECT COUNT(*) FROM item_template {where}";
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        var offset = (page - 1) * pageSize;
        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        var dataSql = $@"
            SELECT entry, name, class, subclass, quality, display_id AS displayId,
                   inventory_type AS inventoryType, required_level AS requiredLevel,
                   item_level AS itemLevel, description,
                   buy_price AS buyPrice, sell_price AS sellPrice,
                   bonding, stackable, max_count AS maxCount,
                   armor, block,
                   dmg_min1 AS dmgMin1, dmg_max1 AS dmgMax1, dmg_type1 AS dmgType1, delay,
                   stat_type1 AS statType1, stat_value1 AS statValue1,
                   stat_type2 AS statType2, stat_value2 AS statValue2,
                   stat_type3 AS statType3, stat_value3 AS statValue3,
                   stat_type4 AS statType4, stat_value4 AS statValue4,
                   stat_type5 AS statType5, stat_value5 AS statValue5,
                   spellid_1 AS spellId1, spelltrigger_1 AS spellTrigger1,
                   spellid_2 AS spellId2, spelltrigger_2 AS spellTrigger2
            FROM item_template {where}
            ORDER BY entry ASC
            LIMIT @PageSize OFFSET @Offset";

        var items = (await conn.QueryAsync<dynamic>(dataSql, parameters)).ToList();

        var iconMap = new Dictionary<uint, string>();
        foreach (var item in items)
        {
            uint did = (uint)(item.displayId ?? 0);
            if (did > 0 && !iconMap.ContainsKey(did))
                iconMap[did] = _dbc.GetItemIconPath(did);
        }

        return Json(new
        {
            items,
            icons = iconMap,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    // ===================== DETAIL (existing, unchanged) =====================

    /// <summary>
    /// GET /Items/Detail?entry=19019 — Full item details for the detail panel.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail(int entry)
    {
        using var conn = _db.Mangos();

        var sql = @"
            SELECT *
            FROM item_template
            WHERE entry = @Entry
            ORDER BY patch DESC
            LIMIT 1";

        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { Entry = entry });
        if (item == null)
            return Json(new { found = false });

        uint displayId = (uint)(item.display_id ?? 0);
        var iconPath = _dbc.GetItemIconPath(displayId);

        // Check for 3D model
        string? modelPath = null;
        var glbFile = Path.Combine(_env.WebRootPath, "item_models", $"{displayId}.glb");
        if (System.IO.File.Exists(glbFile))
            modelPath = $"/item_models/{displayId}.glb";

        return Json(new { found = true, item, iconPath, modelPath });
    }

    // ===================== NEW — EDIT ENDPOINTS =====================

    /// <summary>
    /// GET /Items/NextCustomId — returns the next available entry in the 900000+ range.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> NextCustomId()
    {
        using var conn = _db.Mangos();
        var maxEntry = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM item_template WHERE entry >= @Start",
            new { Start = CUSTOM_RANGE_START });

        var nextId = (maxEntry ?? CUSTOM_RANGE_START - 1) + 1;
        return Json(new { nextId });
    }

    /// <summary>
    /// GET /Items/FullRow?entry=19019 — returns ALL editable columns for an item.
    /// Used to populate the edit form (both for cloning and editing).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> FullRow(int entry)
    {
        using var conn = _db.Mangos();

        var sql = @"SELECT * FROM item_template
                    WHERE entry = @Entry
                    ORDER BY patch DESC LIMIT 1";

        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { Entry = entry });
        if (item == null)
            return Json(new { found = false });

        uint displayId = (uint)(item.display_id ?? 0);
        var iconPath = _dbc.GetItemIconPath(displayId);

        // Check for 3D model
        string? modelPath = null;
        var glbFile = Path.Combine(_env.WebRootPath, "item_models", $"{displayId}.glb");
        if (System.IO.File.Exists(glbFile))
            modelPath = $"/item_models/{displayId}.glb";

        return Json(new
        {
            found = true,
            item,
            iconPath,
            modelPath,
            isCustom = entry >= CUSTOM_RANGE_START
        });
    }

    /// <summary>
    /// POST /Items/Save — Insert (new custom item) or Update (existing item).
    /// Body: JSON with "entry" and all editable field values using snake_case column names.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] JsonElement body)
    {
        if (!body.TryGetProperty("entry", out var entryProp))
            return Json(new { success = false, error = "Missing entry field" });

        int entry = entryProp.GetInt32();

        using var conn = _db.Mangos();

        // Check if this entry already exists
        var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT entry, name FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        // Build state_before for audit
        string? stateBefore = null;
        if (existing != null)
        {
            var beforeRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            stateBefore = JsonSerializer.Serialize((IDictionary<string, object>)beforeRow);
        }

        bool isInsert = existing == null;
        bool isCustom = entry >= CUSTOM_RANGE_START;

        // Build parameter dictionary from the JSON body
        var parameters = new DynamicParameters();
        parameters.Add("Entry", entry);

        // For new items, use patch=0 (custom content, no progressive patching)
        if (isInsert)
            parameters.Add("Patch", 0);

        foreach (var col in EDITABLE_COLUMNS)
        {
            // Try to get the value from the JSON body using the column name
            if (body.TryGetProperty(col, out var val))
            {
                if (val.ValueKind == JsonValueKind.Null || val.ValueKind == JsonValueKind.Undefined)
                    parameters.Add(col, 0);
                else if (val.ValueKind == JsonValueKind.Number)
                    parameters.Add(col, val.GetDouble());
                else if (val.ValueKind == JsonValueKind.String)
                    parameters.Add(col, val.GetString());
                else
                    parameters.Add(col, val.GetRawText());
            }
            else
            {
                // Default to 0 for missing numeric fields, empty for strings
                if (col == "name")
                    parameters.Add(col, "Custom Item");
                else if (col == "description")
                    parameters.Add(col, "");
                else
                    parameters.Add(col, 0);
            }
        }

        try
        {
            if (isInsert)
            {
                // INSERT new item
                var columns = "entry, patch, " + string.Join(", ", EDITABLE_COLUMNS);
                var values = "@Entry, @Patch, " + string.Join(", ", EDITABLE_COLUMNS.Select(c => "@" + c));

                var insertSql = $"INSERT INTO item_template ({columns}) VALUES ({values})";
                await conn.ExecuteAsync(insertSql, parameters);
            }
            else
            {
                // UPDATE existing item — update the latest patch row
                var patch = await conn.ExecuteScalarAsync<int>(
                    "SELECT MAX(patch) FROM item_template WHERE entry = @Entry",
                    new { Entry = entry });
                parameters.Add("Patch", patch);

                var setClauses = string.Join(", ", EDITABLE_COLUMNS.Select(c => $"{c} = @{c}"));
                var updateSql = $"UPDATE item_template SET {setClauses} WHERE entry = @Entry AND patch = @Patch";
                await conn.ExecuteAsync(updateSql, parameters);
            }

            // Build state_after for audit
            var afterRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
                new { Entry = entry });
            var stateAfter = afterRow != null
                ? JsonSerializer.Serialize((IDictionary<string, object>)afterRow)
                : null;

            // Get the item name for the audit log
            string itemName = "Unknown";
            if (body.TryGetProperty("name", out var nameProp))
                itemName = nameProp.GetString() ?? "Unknown";

            // Audit log
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "content",
                Action = isInsert ? "item_create" : "item_edit",
                TargetType = isCustom ? "item_custom" : "item_base_game",
                TargetName = itemName,
                TargetId = entry,
                StateBefore = stateBefore,
                StateAfter = stateAfter,
                IsReversible = true,
                Success = true,
                Notes = isInsert
                    ? $"Created custom item #{entry}"
                    : (isCustom ? $"Edited custom item #{entry}" : $"Edited base game item #{entry}")
            });

            return Json(new { success = true, entry, isInsert });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Items/Delete?entry=N — Delete a custom item (900000+ only).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete(int entry)
    {
        if (entry < CUSTOM_RANGE_START)
            return Json(new { success = false, error = "Cannot delete base game items" });

        using var conn = _db.Mangos();

        // Get state before for audit
        var beforeRow = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (beforeRow == null)
            return Json(new { success = false, error = "Item not found" });

        string stateBefore = JsonSerializer.Serialize((IDictionary<string, object>)beforeRow);
        string itemName = (string)(beforeRow.name ?? "Unknown");

        await conn.ExecuteAsync("DELETE FROM item_template WHERE entry = @Entry", new { Entry = entry });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "item_delete",
            TargetType = "item_custom",
            TargetName = itemName,
            TargetId = entry,
            StateBefore = stateBefore,
            IsReversible = false,
            Success = true,
            Notes = $"Deleted custom item #{entry}"
        });

        return Json(new { success = true });
    }

    // ===================== MODEL CHECK =====================

    /// <summary>
    /// GET /Items/ModelExists?displayId=6 — Quick check if an item GLB exists.
    /// </summary>
    [HttpGet]
    public IActionResult ModelExists(uint displayId)
    {
        var glbFile = Path.Combine(_env.WebRootPath, "item_models", $"{displayId}.glb");
        return Json(new { exists = System.IO.File.Exists(glbFile), path = $"/item_models/{displayId}.glb" });
    }

    // ===================== ICON SEARCH =====================

    /// <summary>
    /// GET /Items/IconSearch?q=sword&page=1&pageSize=60
    /// Searches icon filenames from the DBC data for the icon picker.
    /// Returns icons with their associated displayIds.
    /// </summary>
    [HttpGet]
    public IActionResult IconSearch(string? q, int page = 1, int pageSize = 60)
    {
        var reverseMap = _dbc.GetIconToDisplayIds();

        IEnumerable<KeyValuePair<string, List<uint>>> filtered = reverseMap;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLowerInvariant();
            filtered = reverseMap.Where(kv => kv.Key.Contains(search));
        }

        var sorted = filtered.OrderBy(kv => kv.Key).ToList();
        var totalCount = sorted.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize);

        var results = paged.Select(kv => new
        {
            iconName = kv.Key,
            iconPath = $"/icons/{kv.Key}.png",
            displayIds = kv.Value
        });

        return Json(new
        {
            icons = results,
            totalCount,
            page,
            pageSize,
            totalPages
        });
    }
}