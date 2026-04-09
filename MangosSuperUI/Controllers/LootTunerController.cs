using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class LootTunerController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;

    // All loot table names that share the same schema
    private static readonly Dictionary<string, string> LOOT_TABLES = new()
    {
        ["creature"] = "creature_loot_template",
        ["gameobject"] = "gameobject_loot_template",
        ["fishing"] = "fishing_loot_template",
        ["item"] = "item_loot_template",
        ["reference"] = "reference_loot_template",
        ["disenchant"] = "disenchant_loot_template",
        ["skinning"] = "skinning_loot_template",
        ["pickpocketing"] = "pickpocketing_loot_template",
        ["mail"] = "mail_loot_template"
    };

    // Vanilla WoW dungeon/raid map IDs → names
    private static readonly Dictionary<int, string> MAP_NAMES = new()
    {
        [30] = "Alterac Valley",
        [33] = "Shadowfang Keep",
        [34] = "The Stockade",
        [36] = "Deadmines",
        [43] = "Wailing Caverns",
        [47] = "Razorfen Kraul",
        [48] = "Blackfathom Deeps",
        [70] = "Uldaman",
        [90] = "Gnomeregan",
        [109] = "Sunken Temple",
        [129] = "Razorfen Downs",
        [189] = "Scarlet Monastery",
        [209] = "Zul'Farrak",
        [229] = "Blackrock Spire",
        [230] = "Blackrock Depths",
        [249] = "Onyxia's Lair",
        [269] = "Opening of the Dark Portal",
        [289] = "Scholomance",
        [309] = "Zul'Gurub",
        [329] = "Stratholme",
        [349] = "Maraudon",
        [369] = "Deeprun Tram",
        [389] = "Ragefire Chasm",
        [409] = "Molten Core",
        [429] = "Dire Maul",
        [449] = "Alliance PVP Barracks",
        [450] = "Horde PVP Barracks",
        [451] = "Development Land",
        [469] = "Blackwing Lair",
        [489] = "Warsong Gulch",
        [509] = "Ruins of Ahn'Qiraj",
        [529] = "Arathi Basin",
        [531] = "Temple of Ahn'Qiraj",
        [533] = "Naxxramas"
    };

    // Creature ranks
    private static readonly Dictionary<int, string> RANK_NAMES = new()
    {
        [0] = "Normal",
        [1] = "Elite",
        [2] = "Rare Elite",
        [3] = "Boss",
        [4] = "Rare"
    };

    public LootTunerController(ConnectionFactory db, DbcService dbc, AuditService audit)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
    }

    public IActionResult Index() => View();

    // ===================== METADATA =====================

    /// <summary>
    /// GET /LootTuner/Meta — Returns maps, ranks, loot table names for filter dropdowns.
    /// </summary>
    [HttpGet]
    public IActionResult Meta()
    {
        return Json(new
        {
            maps = MAP_NAMES.OrderBy(kv => kv.Value).Select(kv => new { id = kv.Key, name = kv.Value }),
            ranks = RANK_NAMES.Select(kv => new { id = kv.Key, name = kv.Value }),
            lootTables = LOOT_TABLES.Select(kv => new { key = kv.Key, table = kv.Value })
        });
    }

    // ===================== PREVIEW =====================

    /// <summary>
    /// POST /LootTuner/Preview — Queries matching loot entries based on filters.
    /// Returns the items that would be affected, with current drop rates.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] LootFilterRequest filter)
    {
        using var conn = _db.Mangos();

        // Determine which loot tables to query
        var tables = GetTargetTables(filter);
        var allResults = new List<LootPreviewRow>();

        foreach (var table in tables)
        {
            var results = await QueryLootTable(conn, table.key, table.tableName, filter);
            allResults.AddRange(results);
        }

        // Sort by source, then item name
        allResults = allResults.OrderBy(r => r.sourceName).ThenBy(r => r.itemName).ToList();

        // Summary stats
        var totalEntries = allResults.Count;
        var uniqueItems = allResults.Select(r => r.itemEntry).Distinct().Count();
        var uniqueSources = allResults.Select(r => r.lootEntry).Distinct().Count();
        var tableBreakdown = allResults.GroupBy(r => r.tableKey)
            .Select(g => new { table = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count);

        // Batch resolve icons for the items
        var iconMap = new Dictionary<uint, string>();
        foreach (var row in allResults)
        {
            if (row.displayId > 0 && !iconMap.ContainsKey(row.displayId))
                iconMap[row.displayId] = _dbc.GetItemIconPath(row.displayId);
        }

        // Cap results for preview (show first 500, report total)
        var previewRows = allResults.Take(500).ToList();

        return Json(new
        {
            totalEntries,
            uniqueItems,
            uniqueSources,
            tableBreakdown,
            icons = iconMap,
            rows = previewRows,
            truncated = totalEntries > 500
        });
    }

    // ===================== APPLY =====================

    /// <summary>
    /// POST /LootTuner/Apply — Applies the multiplier to all matching loot entries.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Apply([FromBody] LootApplyRequest request)
    {
        if (request.multiplier <= 0 || request.multiplier > 100)
            return Json(new { success = false, error = "Multiplier must be between 0.01 and 100" });

        using var conn = _db.Mangos();

        var tables = GetTargetTables(request.filter);
        int totalUpdated = 0;
        var auditDetails = new List<object>();

        foreach (var table in tables)
        {
            var rows = await QueryLootTable(conn, table.key, table.tableName, request.filter);
            if (rows.Count == 0) continue;

            // Capture before state
            var beforeState = rows.Select(r => new
            {
                r.tableKey,
                r.lootEntry,
                r.itemEntry,
                r.currentChance,
                r.groupId,
                r.patchMin,
                r.patchMax
            }).ToList();

            // Build UPDATE — we update each matching row's ChanceOrQuestChance
            // For quest chances (negative values), we multiply the absolute value
            // Cap at 100 (or -100 for quest items)
            foreach (var row in rows)
            {
                float oldChance = row.currentChance;
                float newChance;

                if (oldChance < 0)
                {
                    // Quest chance — negative value, multiply absolute
                    newChance = Math.Max(-100f, oldChance * request.multiplier);
                }
                else
                {
                    newChance = Math.Min(100f, oldChance * request.multiplier);
                }

                // Round to 4 decimal places
                newChance = (float)Math.Round(newChance, 4);

                await conn.ExecuteAsync(
                    $@"UPDATE `{table.tableName}` 
                       SET ChanceOrQuestChance = @NewChance 
                       WHERE entry = @Entry AND item = @Item 
                         AND groupid = @GroupId AND patch_min = @PatchMin AND patch_max = @PatchMax",
                    new
                    {
                        NewChance = newChance,
                        Entry = row.lootEntry,
                        Item = row.itemEntry,
                        GroupId = row.groupId,
                        PatchMin = row.patchMin,
                        PatchMax = row.patchMax
                    });

                totalUpdated++;
            }

            auditDetails.Add(new
            {
                table = table.tableName,
                rowsUpdated = rows.Count,
                before = beforeState
            });
        }

        // Audit log
        var filterDesc = BuildFilterDescription(request.filter);
        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "loot_tune",
            TargetType = "loot_tables",
            TargetName = filterDesc,
            StateBefore = JsonSerializer.Serialize(auditDetails),
            StateAfter = JsonSerializer.Serialize(new { multiplier = request.multiplier, totalUpdated }),
            IsReversible = true,
            Success = true,
            Notes = $"Loot tuning: {request.multiplier}x multiplier applied to {totalUpdated} entries. Filter: {filterDesc}"
        });

        return Json(new
        {
            success = true,
            totalUpdated,
            multiplier = request.multiplier,
            description = filterDesc
        });
    }

    // ===================== RESET TO BASELINE =====================

    /// <summary>
    /// Tables that have OG baselines in vmangos_admin.
    /// Maps loot table key → (mangos table, og table).
    /// </summary>
    private static readonly Dictionary<string, (string mangos, string og)> OG_TABLES = new()
    {
        ["creature"] = ("creature_loot_template", "og_creature_loot_template"),
        ["gameobject"] = ("gameobject_loot_template", "og_gameobject_loot_template"),
        ["reference"] = ("reference_loot_template", "og_reference_loot_template"),
        ["fishing"] = ("fishing_loot_template", "og_fishing_loot_template"),
        ["skinning"] = ("skinning_loot_template", "og_skinning_loot_template"),
        ["pickpocketing"] = ("pickpocketing_loot_template", "og_pickpocketing_loot_template"),
        ["disenchant"] = ("disenchant_loot_template", "og_disenchant_loot_template"),
    };

    /// <summary>
    /// POST /LootTuner/ResetToBaseline — Restores all loot tables from OG snapshots.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetToBaseline()
    {
        // Verify baseline exists
        using var adminConn = _db.Admin();
        var metaCount = await adminConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM og_baseline_meta WHERE table_name LIKE 'og_%_loot_template'");

        if (metaCount == 0)
            return Json(new { success = false, error = "OG baseline has not been initialized. Run baseline initialization first." });

        using var mangosConn = _db.Mangos();
        int totalRestored = 0;
        var details = new List<object>();

        foreach (var (key, (mangosTable, ogTable)) in OG_TABLES)
        {
            // Check that the OG table exists and has data
            var ogExists = await adminConn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM og_baseline_meta WHERE table_name = @T",
                new { T = ogTable });

            if (ogExists == 0) continue;

            // Get current row count for audit
            var currentCount = await mangosConn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM `{mangosTable}`");

            // Truncate + restore from OG
            await mangosConn.ExecuteAsync($"DELETE FROM `{mangosTable}`");
            var restored = await mangosConn.ExecuteAsync(
                $"INSERT INTO `{mangosTable}` SELECT * FROM `vmangos_admin`.`{ogTable}`");

            totalRestored += restored;
            details.Add(new { table = mangosTable, previousRows = currentCount, restoredRows = restored });
        }

        // Audit log
        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "loot_reset_baseline",
            TargetType = "loot_tables",
            TargetName = "all loot tables",
            StateBefore = JsonSerializer.Serialize(details),
            StateAfter = JsonSerializer.Serialize(new { totalRestored }),
            IsReversible = false,
            Success = true,
            Notes = $"Full loot table reset to OG baseline. {totalRestored} total rows restored across {details.Count} tables."
        });

        return Json(new { success = true, totalRestored, tables = details });
    }

    // ===================== CHANGELOG =====================

    /// <summary>
    /// GET /LootTuner/Changelog — Diffs current loot tables against OG baselines.
    /// Returns rows where ChanceOrQuestChance differs.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Changelog()
    {
        using var adminConn = _db.Admin();
        var metaCount = await adminConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM og_baseline_meta WHERE table_name LIKE 'og_%_loot_template'");

        if (metaCount == 0)
            return Json(new { initialized = false, changes = Array.Empty<object>() });

        using var mangosConn = _db.Mangos();
        var allChanges = new List<LootChangelogRow>();

        foreach (var (key, (mangosTable, ogTable)) in OG_TABLES)
        {
            var ogExists = await adminConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM og_baseline_meta WHERE table_name = @T",
                new { T = ogTable });
            if (ogExists == 0) continue;

            // Find rows where chance differs between current and OG
            var sql = $@"
                SELECT 
                    cur.entry       AS lootEntry,
                    cur.item        AS itemEntry,
                    cur.ChanceOrQuestChance AS currentChance,
                    og.ChanceOrQuestChance  AS originalChance,
                    cur.groupid     AS groupId,
                    it.name         AS itemName,
                    it.quality      AS itemQuality,
                    it.display_id   AS displayId,
                    '{key}'         AS tableKey
                FROM `{mangosTable}` cur
                JOIN `vmangos_admin`.`{ogTable}` og 
                    ON og.entry = cur.entry 
                    AND og.item = cur.item 
                    AND og.groupid = cur.groupid
                    AND og.patch_min = cur.patch_min
                    AND og.patch_max = cur.patch_max
                JOIN item_template it 
                    ON it.entry = cur.item 
                    AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)
                WHERE cur.ChanceOrQuestChance != og.ChanceOrQuestChance
                ORDER BY it.quality DESC, it.name
                LIMIT 500";

            var rows = (await mangosConn.QueryAsync<LootChangelogRow>(sql)).ToList();
            allChanges.AddRange(rows);
        }

        // Resolve icons
        var iconMap = new Dictionary<uint, string>();
        foreach (var row in allChanges)
        {
            if (row.displayId > 0 && !iconMap.ContainsKey(row.displayId))
                iconMap[row.displayId] = _dbc.GetItemIconPath(row.displayId);
        }

        // Summary
        var totalChanged = allChanges.Count;
        var tableBreakdown = allChanges.GroupBy(r => r.tableKey)
            .Select(g => new { table = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count);

        return Json(new
        {
            initialized = true,
            totalChanged,
            tableBreakdown,
            icons = iconMap,
            changes = allChanges.Take(500)
        });
    }

    // ===================== STATS =====================

    /// <summary>
    /// GET /LootTuner/Stats — Quick overview stats for the dashboard card.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Stats()
    {
        using var conn = _db.Mangos();

        var counts = await conn.QueryAsync<dynamic>(
            @"SELECT 'creature' AS tbl, COUNT(*) AS cnt FROM creature_loot_template
              UNION ALL SELECT 'gameobject', COUNT(*) FROM gameobject_loot_template
              UNION ALL SELECT 'reference', COUNT(*) FROM reference_loot_template
              UNION ALL SELECT 'fishing', COUNT(*) FROM fishing_loot_template
              UNION ALL SELECT 'skinning', COUNT(*) FROM skinning_loot_template
              UNION ALL SELECT 'pickpocketing', COUNT(*) FROM pickpocketing_loot_template
              UNION ALL SELECT 'disenchant', COUNT(*) FROM disenchant_loot_template");

        return Json(counts);
    }

    // ===================== HELPERS =====================

    private List<(string key, string tableName)> GetTargetTables(LootFilterRequest filter)
    {
        if (!string.IsNullOrEmpty(filter.lootSource) && filter.lootSource != "all")
        {
            if (LOOT_TABLES.TryGetValue(filter.lootSource, out var tableName))
                return new List<(string, string)> { (filter.lootSource, tableName) };
        }

        // Default: creature + gameobject + reference (the three main ones)
        // Don't include fishing/skinning/pickpocketing/disenchant/mail unless specifically selected
        if (filter.lootSource == "all")
        {
            return LOOT_TABLES
                .Where(kv => kv.Key != "mail") // mail loot is rarely relevant
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }

        // Default scope: creature + gameobject (the most impactful ones)
        return new List<(string, string)>
        {
            ("creature", "creature_loot_template"),
            ("gameobject", "gameobject_loot_template")
        };
    }

    private async Task<List<LootPreviewRow>> QueryLootTable(
        MySqlConnector.MySqlConnection conn,
        string tableKey,
        string tableName,
        LootFilterRequest filter)
    {
        var parameters = new DynamicParameters();
        var joins = new List<string>();
        var wheres = new List<string>();

        // Base: only actual items (mincountOrRef > 0), not reference pointers
        // Reference pointers have mincountOrRef < 0
        wheres.Add("lt.mincountOrRef > 0");

        // Join item_template to get quality, class, slot, level, name, displayId
        joins.Add(@"JOIN item_template it ON it.entry = lt.item 
                    AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)");

        // Filter: quality
        if (filter.qualities != null && filter.qualities.Length > 0)
        {
            wheres.Add("it.quality IN @Qualities");
            parameters.Add("Qualities", filter.qualities);
        }

        // Filter: item class
        if (filter.itemClasses != null && filter.itemClasses.Length > 0)
        {
            wheres.Add("it.class IN @ItemClasses");
            parameters.Add("ItemClasses", filter.itemClasses);
        }

        // Filter: inventory slot
        if (filter.slots != null && filter.slots.Length > 0)
        {
            wheres.Add("it.inventory_type IN @Slots");
            parameters.Add("Slots", filter.slots);
        }

        // Filter: item required level range
        if (filter.itemLevelMin.HasValue)
        {
            wheres.Add("it.required_level >= @ItemLevelMin");
            parameters.Add("ItemLevelMin", filter.itemLevelMin.Value);
        }
        if (filter.itemLevelMax.HasValue)
        {
            wheres.Add("it.required_level <= @ItemLevelMax");
            parameters.Add("ItemLevelMax", filter.itemLevelMax.Value);
        }

        // Filter: creature-specific (rank, map, specific creature)
        if (tableKey == "creature" &&
            (filter.creatureRanks != null || filter.mapIds != null || filter.creatureEntry.HasValue))
        {
            // Join creature_template to get rank, level, name
            joins.Add("JOIN creature_template ct ON ct.loot_id = lt.entry AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)");

            if (filter.creatureRanks != null && filter.creatureRanks.Length > 0)
            {
                wheres.Add("ct.rank IN @CreatureRanks");
                parameters.Add("CreatureRanks", filter.creatureRanks);
            }

            if (filter.mapIds != null && filter.mapIds.Length > 0)
            {
                // Join creature spawns to get map
                joins.Add("JOIN creature c ON c.id = ct.entry");
                wheres.Add("c.map IN @MapIds");
                parameters.Add("MapIds", filter.mapIds);
            }

            if (filter.creatureEntry.HasValue)
            {
                wheres.Add("ct.entry = @CreatureEntry");
                parameters.Add("CreatureEntry", filter.creatureEntry.Value);
            }
        }

        // Filter: chance range (to exclude 100% guaranteed drops, or find very rare items)
        if (filter.chanceMin.HasValue)
        {
            wheres.Add("ABS(lt.ChanceOrQuestChance) >= @ChanceMin");
            parameters.Add("ChanceMin", filter.chanceMin.Value);
        }
        if (filter.chanceMax.HasValue)
        {
            wheres.Add("ABS(lt.ChanceOrQuestChance) <= @ChanceMax");
            parameters.Add("ChanceMax", filter.chanceMax.Value);
        }

        // Exclude 100% drops by default (these are guaranteed, not tunable)
        if (!filter.includeGuaranteed)
        {
            wheres.Add("ABS(lt.ChanceOrQuestChance) < 100");
        }

        var whereClause = wheres.Count > 0 ? "WHERE " + string.Join(" AND ", wheres) : "";
        var joinClause = string.Join(" ", joins);

        // Build source name based on table type
        string sourceNameExpr;
        if (tableKey == "creature" && joins.Any(j => j.Contains("creature_template")))
            sourceNameExpr = "ct.name";
        else
            sourceNameExpr = "CONCAT('" + tableKey + " #', lt.entry)";

        var sql = $@"SELECT DISTINCT
                lt.entry AS lootEntry,
                lt.item AS itemEntry,
                lt.ChanceOrQuestChance AS currentChance,
                lt.groupid AS groupId,
                lt.patch_min AS patchMin,
                lt.patch_max AS patchMax,
                it.name AS itemName,
                it.quality AS itemQuality,
                it.class AS itemClass,
                it.inventory_type AS itemSlot,
                it.display_id AS displayId,
                it.required_level AS itemReqLevel,
                {sourceNameExpr} AS sourceName,
                '{tableKey}' AS tableKey
            FROM `{tableName}` lt
            {joinClause}
            {whereClause}
            ORDER BY lt.entry, it.quality DESC, it.name
            LIMIT 2000";

        var results = (await conn.QueryAsync<LootPreviewRow>(sql, parameters)).ToList();
        return results;
    }

    private string BuildFilterDescription(LootFilterRequest filter)
    {
        var parts = new List<string>();

        if (filter.lootSource != null && filter.lootSource != "all")
            parts.Add($"source={filter.lootSource}");

        if (filter.qualities != null && filter.qualities.Length > 0)
        {
            var names = filter.qualities.Select(q => q switch
            {
                0 => "Poor",
                1 => "Common",
                2 => "Uncommon",
                3 => "Rare",
                4 => "Epic",
                5 => "Legendary",
                _ => $"Q{q}"
            });
            parts.Add($"quality=[{string.Join(",", names)}]");
        }

        if (filter.creatureRanks != null && filter.creatureRanks.Length > 0)
        {
            var names = filter.creatureRanks.Select(r =>
                RANK_NAMES.TryGetValue(r, out var n) ? n : $"Rank{r}");
            parts.Add($"rank=[{string.Join(",", names)}]");
        }

        if (filter.mapIds != null && filter.mapIds.Length > 0)
        {
            var names = filter.mapIds.Select(m =>
                MAP_NAMES.TryGetValue(m, out var n) ? n : $"Map{m}");
            parts.Add($"map=[{string.Join(",", names)}]");
        }

        if (filter.itemLevelMin.HasValue || filter.itemLevelMax.HasValue)
            parts.Add($"level={filter.itemLevelMin ?? 1}-{filter.itemLevelMax ?? 60}");

        if (filter.creatureEntry.HasValue)
            parts.Add($"creature={filter.creatureEntry}");

        return parts.Count > 0 ? string.Join(", ", parts) : "all loot";
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────

public class LootFilterRequest
{
    public string? lootSource { get; set; }          // "creature", "gameobject", "all", etc.
    public int[]? qualities { get; set; }             // [3, 4] = Rare + Epic
    public int[]? itemClasses { get; set; }           // [2, 4] = Weapon + Armor
    public int[]? slots { get; set; }                 // [12] = Trinket
    public int? itemLevelMin { get; set; }
    public int? itemLevelMax { get; set; }
    public int[]? creatureRanks { get; set; }         // [3] = Boss only
    public int[]? mapIds { get; set; }                // [409] = Molten Core
    public int? creatureEntry { get; set; }           // Specific creature
    public float? chanceMin { get; set; }             // Min drop chance
    public float? chanceMax { get; set; }             // Max drop chance
    public bool includeGuaranteed { get; set; }       // Include 100% drops?
}

public class LootApplyRequest
{
    public LootFilterRequest filter { get; set; } = new();
    public float multiplier { get; set; } = 1.0f;
}

public class LootPreviewRow
{
    public int lootEntry { get; set; }
    public int itemEntry { get; set; }
    public float currentChance { get; set; }
    public int groupId { get; set; }
    public int patchMin { get; set; }
    public int patchMax { get; set; }
    public string itemName { get; set; } = "";
    public int itemQuality { get; set; }
    public int itemClass { get; set; }
    public int itemSlot { get; set; }
    public uint displayId { get; set; }
    public int itemReqLevel { get; set; }
    public string sourceName { get; set; } = "";
    public string tableKey { get; set; } = "";
}

public class LootChangelogRow
{
    public int lootEntry { get; set; }
    public int itemEntry { get; set; }
    public float currentChance { get; set; }
    public float originalChance { get; set; }
    public int groupId { get; set; }
    public string itemName { get; set; } = "";
    public int itemQuality { get; set; }
    public uint displayId { get; set; }
    public string tableKey { get; set; } = "";
}