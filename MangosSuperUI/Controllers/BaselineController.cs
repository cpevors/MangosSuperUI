using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class BaselineController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly ILogger<BaselineController> _logger;

    // Tables to snapshot: og_name → (source_table, source_database)
    private static readonly (string ogTable, string sourceTable)[] SNAPSHOT_TABLES = new[]
    {
        ("og_item_template",                "item_template"),
        ("og_creature_loot_template",       "creature_loot_template"),
        ("og_gameobject_loot_template",     "gameobject_loot_template"),
        ("og_reference_loot_template",      "reference_loot_template"),
        ("og_fishing_loot_template",        "fishing_loot_template"),
        ("og_skinning_loot_template",       "skinning_loot_template"),
        ("og_pickpocketing_loot_template",  "pickpocketing_loot_template"),
        ("og_disenchant_loot_template",     "disenchant_loot_template"),
        ("og_spell_template",               "spell_template"),
        ("og_gameobject_template",          "gameobject_template")
    };

    public BaselineController(ConnectionFactory db, AuditService audit, ILogger<BaselineController> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    // ===================== STATUS =====================

    /// <summary>
    /// GET /Baseline/Status — Returns which OG tables exist and their row counts.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Status()
    {
        using var admin = _db.Admin();

        // Check if meta table exists
        var metaExists = await TableExists(admin, "og_baseline_meta");
        if (!metaExists)
        {
            return Json(new
            {
                initialized = false,
                tables = Array.Empty<object>()
            });
        }

        var rows = await admin.QueryAsync<dynamic>(
            "SELECT table_name AS tableName, source_table AS sourceTable, row_count AS rowCount, created_at AS createdAt FROM og_baseline_meta ORDER BY id");

        var tableList = rows.ToList();

        return Json(new
        {
            initialized = tableList.Count > 0,
            tables = tableList
        });
    }

    // ===================== INITIALIZE =====================

    /// <summary>
    /// POST /Baseline/Initialize — Creates all OG tables and copies data.
    /// This is the one-time setup. Idempotent — will skip tables that already exist.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Initialize()
    {
        using var admin = _db.Admin();

        // Ensure meta table exists
        await admin.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS og_baseline_meta (
                id              INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                table_name      VARCHAR(64)     NOT NULL,
                source_table    VARCHAR(64)     NOT NULL,
                source_database VARCHAR(64)     NOT NULL DEFAULT 'mangos',
                row_count       INT UNSIGNED    NOT NULL DEFAULT 0,
                created_at      DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                UNIQUE KEY idx_table (table_name)
            ) ENGINE=InnoDB;");

        var results = new List<object>();

        foreach (var (ogTable, sourceTable) in SNAPSHOT_TABLES)
        {
            try
            {
                // Check if OG table already exists and has data
                var exists = await TableExists(admin, ogTable);
                if (exists)
                {
                    var count = await admin.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{ogTable}`");
                    if (count > 0)
                    {
                        results.Add(new { table = ogTable, status = "skipped", rowCount = count, reason = "Already exists with data" });
                        continue;
                    }
                    // Exists but empty — drop and recreate
                    await admin.ExecuteAsync($"DROP TABLE `{ogTable}`");
                }

                // Create table with identical schema using CREATE TABLE ... LIKE
                // Then copy all data
                await admin.ExecuteAsync($"CREATE TABLE `{ogTable}` LIKE `mangos`.`{sourceTable}`");
                await admin.ExecuteAsync($"INSERT INTO `{ogTable}` SELECT * FROM `mangos`.`{sourceTable}`");

                var rowCount = await admin.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{ogTable}`");

                // Record in meta table
                await admin.ExecuteAsync(@"
                    INSERT INTO og_baseline_meta (table_name, source_table, source_database, row_count)
                    VALUES (@OgTable, @SourceTable, 'mangos', @RowCount)
                    ON DUPLICATE KEY UPDATE row_count = @RowCount, created_at = CURRENT_TIMESTAMP(3)",
                    new { OgTable = ogTable, SourceTable = sourceTable, RowCount = rowCount });

                results.Add(new { table = ogTable, status = "created", rowCount });

                _logger.LogInformation("OG baseline: Created {OgTable} with {Count} rows from {Source}",
                    ogTable, rowCount, sourceTable);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create OG baseline table {Table}", ogTable);
                results.Add(new { table = ogTable, status = "error", rowCount = 0, reason = ex.Message });
            }
        }

        // Audit log
        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "system",
            Action = "baseline_initialize",
            TargetType = "baseline",
            TargetName = "og_baseline",
            StateAfter = JsonSerializer.Serialize(results),
            Success = true,
            Notes = $"Initialized OG baseline tables ({results.Count} tables)"
        });

        return Json(new { success = true, tables = results });
    }

    // ===================== DIFF — ITEM =====================

    /// <summary>
    /// GET /Baseline/DiffItem?entry=N — Compares current item_template vs og_item_template.
    /// Returns field-level diff.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DiffItem(int entry)
    {
        using var admin = _db.Admin();

        if (!await TableExists(admin, "og_item_template"))
            return Json(new { available = false, reason = "Baseline not initialized" });

        // Get OG row
        var ogRow = await admin.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM og_item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (ogRow == null)
        {
            // Custom item (900000+) or item added after baseline
            return Json(new { available = true, isCustom = entry >= 900000, hasOriginal = false, changes = new object[0] });
        }

        // Get current row from mangos
        using var mangos = _db.Mangos();
        var currentRow = await mangos.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM item_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (currentRow == null)
        {
            return Json(new { available = true, hasOriginal = true, deleted = true, changes = new object[0] });
        }

        // Compare all fields
        var ogDict = (IDictionary<string, object>)ogRow;
        var curDict = (IDictionary<string, object>)currentRow;
        var changes = BuildDiff(ogDict, curDict);

        return Json(new
        {
            available = true,
            hasOriginal = true,
            isCustom = false,
            isModified = changes.Count > 0,
            changes
        });
    }

    // ===================== DIFF — SPELL =====================

    /// <summary>
    /// GET /Baseline/DiffSpell?entry=N — Compares current spell_template vs og_spell_template.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DiffSpell(int entry)
    {
        using var admin = _db.Admin();

        if (!await TableExists(admin, "og_spell_template"))
            return Json(new { available = false, reason = "Spell baseline not initialized" });

        var ogRow = await admin.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM og_spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
            new { Entry = entry });

        if (ogRow == null)
            return Json(new { available = true, hasOriginal = false, changes = new object[0] });

        using var mangos = _db.Mangos();
        var currentRow = await mangos.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
            new { Entry = entry });

        if (currentRow == null)
            return Json(new { available = true, hasOriginal = true, deleted = true, changes = new object[0] });

        var ogDict = (IDictionary<string, object>)ogRow;
        var curDict = (IDictionary<string, object>)currentRow;
        var changes = BuildDiff(ogDict, curDict);

        return Json(new
        {
            available = true,
            hasOriginal = true,
            isModified = changes.Count > 0,
            changes
        });
    }

    // ===================== DIFF — GAME OBJECT =====================

    /// <summary>
    /// GET /Baseline/DiffGameObject?entry=N — Compares current gameobject_template vs og_gameobject_template.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DiffGameObject(int entry)
    {
        using var admin = _db.Admin();

        if (!await TableExists(admin, "og_gameobject_template"))
            return Json(new { available = false, reason = "Game object baseline not initialized" });

        var ogRow = await admin.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM og_gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (ogRow == null)
            return Json(new { available = true, hasOriginal = false, isCustom = entry >= 900000, changes = new object[0] });

        using var mangos = _db.Mangos();
        var currentRow = await mangos.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry });

        if (currentRow == null)
            return Json(new { available = true, hasOriginal = true, deleted = true, changes = new object[0] });

        var ogDict = (IDictionary<string, object>)ogRow;
        var curDict = (IDictionary<string, object>)currentRow;
        var changes = BuildDiff(ogDict, curDict);

        return Json(new
        {
            available = true,
            hasOriginal = true,
            isCustom = false,
            isModified = changes.Count > 0,
            changes
        });
    }

    // ===================== RESET — GAME OBJECT =====================

    /// <summary>
    /// POST /Baseline/ResetGameObject?entry=N — Restores a single game object from OG baseline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetGameObject(int entry)
    {
        if (entry >= 900000)
            return Json(new { success = false, error = "Cannot reset custom objects — use delete instead" });

        using var admin = _db.Admin();
        if (!await TableExists(admin, "og_gameobject_template"))
            return Json(new { success = false, error = "Game object baseline not initialized" });

        var ogRows = (await admin.QueryAsync<dynamic>(
            "SELECT * FROM og_gameobject_template WHERE entry = @Entry",
            new { Entry = entry })).ToList();

        if (ogRows.Count == 0)
            return Json(new { success = false, error = "No original data for this game object" });

        using var mangos = _db.Mangos();

        // Capture before state
        var beforeRows = await mangos.QueryAsync<dynamic>(
            "SELECT * FROM gameobject_template WHERE entry = @Entry", new { Entry = entry });
        var stateBefore = JsonSerializer.Serialize(beforeRows.Select(r => (IDictionary<string, object>)r).ToList());

        // Delete current rows and replace with OG
        await mangos.ExecuteAsync("DELETE FROM gameobject_template WHERE entry = @Entry", new { Entry = entry });

        foreach (var ogRow in ogRows)
        {
            var dict = (IDictionary<string, object>)ogRow;
            var columns = string.Join(", ", dict.Keys.Select(k => $"`{k}`"));
            var paramNames = string.Join(", ", dict.Keys.Select(k => $"@{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in dict)
                parameters.Add(kv.Key, kv.Value);
            await mangos.ExecuteAsync($"INSERT INTO gameobject_template ({columns}) VALUES ({paramNames})", parameters);
        }

        var objName = await mangos.ExecuteScalarAsync<string>(
            "SELECT name FROM gameobject_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = entry }) ?? $"GameObject #{entry}";

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_gameobject",
            TargetType = "gameobject_base_game",
            TargetId = entry,
            TargetName = objName,
            StateBefore = stateBefore,
            IsReversible = true,
            Success = true,
            Notes = $"Reset game object #{entry} ({objName}) to original baseline values"
        });

        return Json(new { success = true, entry, objName });
    }

    // ===================== RESET — SPELL =====================

    /// <summary>
    /// POST /Baseline/ResetSpell?entry=N — Restores a single spell from OG baseline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetSpell(int entry)
    {
        using var admin = _db.Admin();
        if (!await TableExists(admin, "og_spell_template"))
            return Json(new { success = false, error = "Spell baseline not initialized" });

        var ogRows = (await admin.QueryAsync<dynamic>(
            "SELECT * FROM og_spell_template WHERE entry = @Entry",
            new { Entry = entry })).ToList();

        if (ogRows.Count == 0)
            return Json(new { success = false, error = "No original data for this spell" });

        using var mangos = _db.Mangos();

        // Capture before state
        var beforeRows = await mangos.QueryAsync<dynamic>(
            "SELECT * FROM spell_template WHERE entry = @Entry", new { Entry = entry });
        var stateBefore = JsonSerializer.Serialize(beforeRows.Select(r => (IDictionary<string, object>)r).ToList());

        // Delete current rows and replace with OG
        await mangos.ExecuteAsync("DELETE FROM spell_template WHERE entry = @Entry", new { Entry = entry });

        foreach (var ogRow in ogRows)
        {
            var dict = (IDictionary<string, object>)ogRow;
            var columns = string.Join(", ", dict.Keys.Select(k => $"`{k}`"));
            var paramNames = string.Join(", ", dict.Keys.Select(k => $"@{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in dict)
                parameters.Add(kv.Key, kv.Value);
            await mangos.ExecuteAsync($"INSERT INTO spell_template ({columns}) VALUES ({paramNames})", parameters);
        }

        var spellName = await mangos.ExecuteScalarAsync<string>(
            "SELECT name FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
            new { Entry = entry }) ?? $"Spell #{entry}";

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_spell",
            TargetType = "spell_template",
            TargetId = entry,
            TargetName = spellName,
            StateBefore = stateBefore,
            IsReversible = true,
            Success = true,
            Notes = $"Reset spell #{entry} ({spellName}) to original baseline values. Restart required."
        });

        return Json(new { success = true, entry, spellName });
    }

    // ===================== DIFF — CREATURE LOOT =====================

    /// <summary>
    /// GET /Baseline/DiffCreatureLoot?creatureEntry=N
    /// Compares all loot for a creature (direct + reference) against OG baseline.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DiffCreatureLoot(int creatureEntry)
    {
        using var admin = _db.Admin();

        if (!await TableExists(admin, "og_creature_loot_template"))
            return Json(new { available = false, reason = "Baseline not initialized" });

        using var mangos = _db.Mangos();

        // Get loot_id from creature_template
        var lootId = await mangos.ExecuteScalarAsync<int?>(
            "SELECT loot_id FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = creatureEntry });

        if (!lootId.HasValue || lootId.Value == 0)
            return Json(new { available = true, hasLoot = false, changes = new object[0] });

        // Compare creature_loot_template rows
        var directChanges = await CompareLootTable(admin, mangos, "creature_loot_template", lootId.Value);

        // Also check reference loot tables that this creature points to
        var refPointers = await mangos.QueryAsync<dynamic>(
            @"SELECT item, mincountOrRef FROM creature_loot_template 
              WHERE entry = @LootId AND mincountOrRef < 0",
            new { LootId = lootId.Value });

        var refChanges = new List<object>();
        if (await TableExists(admin, "og_reference_loot_template"))
        {
            foreach (var ptr in refPointers)
            {
                int refEntry = Math.Abs((int)ptr.mincountOrRef);
                var changes = await CompareLootTable(admin, mangos, "reference_loot_template", refEntry);
                if (changes.Count > 0)
                {
                    refChanges.Add(new { refEntry, changes });
                }
            }
        }

        return Json(new
        {
            available = true,
            hasLoot = true,
            lootId = lootId.Value,
            directChanges,
            refChanges,
            totalChanges = directChanges.Count + refChanges.Count,
            isModified = directChanges.Count > 0 || refChanges.Count > 0
        });
    }

    // ===================== DIFF — LOOT TABLE ENTRY =====================

    /// <summary>
    /// GET /Baseline/DiffLoot?table=creature&entry=N
    /// Compares a specific loot table entry against OG baseline.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DiffLoot(string table, int entry)
    {
        var tableName = table + "_loot_template";
        var ogTableName = "og_" + tableName;

        using var admin = _db.Admin();
        if (!await TableExists(admin, ogTableName))
            return Json(new { available = false, reason = "Baseline not initialized" });

        using var mangos = _db.Mangos();
        var changes = await CompareLootTable(admin, mangos, tableName, entry);

        return Json(new
        {
            available = true,
            changes,
            isModified = changes.Count > 0
        });
    }

    // ===================== RESET — ITEM =====================

    /// <summary>
    /// POST /Baseline/ResetItem?entry=N — Restores a single item from OG baseline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetItem(int entry)
    {
        if (entry >= 900000)
            return Json(new { success = false, error = "Cannot reset custom items — use delete instead" });

        using var admin = _db.Admin();
        if (!await TableExists(admin, "og_item_template"))
            return Json(new { success = false, error = "Baseline not initialized" });

        // Get OG row
        var ogRows = await admin.QueryAsync<dynamic>(
            "SELECT * FROM og_item_template WHERE entry = @Entry",
            new { Entry = entry });

        var ogList = ogRows.ToList();
        if (ogList.Count == 0)
            return Json(new { success = false, error = "No original data for this item" });

        using var mangos = _db.Mangos();

        // Capture before state
        var beforeRows = await mangos.QueryAsync<dynamic>(
            "SELECT * FROM item_template WHERE entry = @Entry", new { Entry = entry });
        var stateBefore = JsonSerializer.Serialize(beforeRows.Select(r => (IDictionary<string, object>)r).ToList());

        // Delete current rows and replace with OG
        await mangos.ExecuteAsync("DELETE FROM item_template WHERE entry = @Entry", new { Entry = entry });

        foreach (var ogRow in ogList)
        {
            var dict = (IDictionary<string, object>)ogRow;
            var columns = string.Join(", ", dict.Keys.Select(k => $"`{k}`"));
            var paramNames = string.Join(", ", dict.Keys.Select(k => $"@{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in dict)
                parameters.Add(kv.Key, kv.Value);
            await mangos.ExecuteAsync($"INSERT INTO item_template ({columns}) VALUES ({paramNames})", parameters);
        }

        // Audit
        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_item",
            TargetType = "item_base_game",
            TargetId = entry,
            StateBefore = stateBefore,
            IsReversible = true,
            Success = true,
            Notes = $"Reset item #{entry} to original baseline values"
        });

        return Json(new { success = true, entry });
    }

    // ===================== RESET — CREATURE LOOT =====================

    /// <summary>
    /// POST /Baseline/ResetCreatureLoot?creatureEntry=N
    /// Restores all loot for a creature from OG baseline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetCreatureLoot(int creatureEntry)
    {
        using var admin = _db.Admin();
        if (!await TableExists(admin, "og_creature_loot_template"))
            return Json(new { success = false, error = "Baseline not initialized" });

        using var mangos = _db.Mangos();

        var lootId = await mangos.ExecuteScalarAsync<int?>(
            "SELECT loot_id FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = creatureEntry });

        if (!lootId.HasValue || lootId.Value == 0)
            return Json(new { success = false, error = "Creature has no loot table" });

        int totalRestored = 0;

        // Reset direct loot
        totalRestored += await ResetLootTableEntry(admin, mangos, "creature_loot_template", lootId.Value);

        // Reset reference tables this creature points to
        if (await TableExists(admin, "og_reference_loot_template"))
        {
            var refPointers = await admin.QueryAsync<int>(
                @"SELECT DISTINCT ABS(mincountOrRef) FROM og_creature_loot_template 
                  WHERE entry = @LootId AND mincountOrRef < 0",
                new { LootId = lootId.Value });

            foreach (var refEntry in refPointers)
            {
                totalRestored += await ResetLootTableEntry(admin, mangos, "reference_loot_template", refEntry);
            }
        }

        var creatureName = await mangos.ExecuteScalarAsync<string>(
            "SELECT name FROM creature_template WHERE entry = @Entry ORDER BY patch DESC LIMIT 1",
            new { Entry = creatureEntry }) ?? $"Creature #{creatureEntry}";

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_creature_loot",
            TargetType = "creature_loot",
            TargetName = creatureName,
            TargetId = creatureEntry,
            IsReversible = false,
            Success = true,
            Notes = $"Reset all loot for {creatureName} to original baseline ({totalRestored} rows)"
        });

        return Json(new { success = true, totalRestored, creatureName });
    }

    // ===================== RESET — INSTANCE =====================

    /// <summary>
    /// POST /Baseline/ResetInstance?mapId=N
    /// Restores all loot for all bosses in an instance.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetInstance(int mapId, [FromServices] IWebHostEnvironment env)
    {
        using var admin = _db.Admin();
        if (!await TableExists(admin, "og_creature_loot_template"))
            return Json(new { success = false, error = "Baseline not initialized" });

        using var mangos = _db.Mangos();

        // Get all creatures with loot in this instance
        var creatures = await mangos.QueryAsync<dynamic>(
            @"SELECT DISTINCT ct.entry, ct.name, ct.loot_id
              FROM creature_template ct
              JOIN creature c ON c.id = ct.entry
              WHERE c.map = @MapId AND ct.loot_id > 0
                AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)",
            new { MapId = mapId });

        int totalRestored = 0;
        int creaturesReset = 0;
        var refEntriesReset = new HashSet<int>();

        foreach (var creature in creatures)
        {
            int lootId = (int)creature.loot_id;

            // Reset direct loot
            totalRestored += await ResetLootTableEntry(admin, mangos, "creature_loot_template", lootId);

            // Reset reference tables
            if (await TableExists(admin, "og_reference_loot_template"))
            {
                var refPointers = await admin.QueryAsync<int>(
                    @"SELECT DISTINCT ABS(mincountOrRef) FROM og_creature_loot_template 
                      WHERE entry = @LootId AND mincountOrRef < 0",
                    new { LootId = lootId });

                foreach (var refEntry in refPointers)
                {
                    if (refEntriesReset.Add(refEntry)) // Only reset each ref table once
                    {
                        totalRestored += await ResetLootTableEntry(admin, mangos, "reference_loot_template", refEntry);
                    }
                }
            }

            creaturesReset++;
        }

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_instance",
            TargetType = "instance",
            TargetId = mapId,
            IsReversible = false,
            Success = true,
            Notes = $"Reset all loot for map {mapId}: {creaturesReset} creatures, {totalRestored} rows"
        });

        return Json(new { success = true, creaturesReset, totalRestored });
    }

    // ===================== RESET — FULL TABLE =====================

    /// <summary>
    /// POST /Baseline/ResetTable?table=creature_loot_template
    /// Restores an entire loot table from OG baseline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetTable(string table)
    {
        var ogTable = "og_" + table;

        using var admin = _db.Admin();
        if (!await TableExists(admin, ogTable))
            return Json(new { success = false, error = $"No baseline for {table}" });

        using var mangos = _db.Mangos();

        var beforeCount = await mangos.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{table}`");

        // Truncate and repopulate from OG
        await mangos.ExecuteAsync($"DELETE FROM `{table}`");
        await mangos.ExecuteAsync($"INSERT INTO `{table}` SELECT * FROM `vmangos_admin`.`{ogTable}`");

        var afterCount = await mangos.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{table}`");

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_table",
            TargetType = "loot_table",
            TargetName = table,
            StateAfter = JsonSerializer.Serialize(new { beforeCount, afterCount }),
            IsReversible = false,
            Success = true,
            Notes = $"Full table reset: {table} ({beforeCount} → {afterCount} rows)"
        });

        return Json(new { success = true, table, beforeCount, afterCount });
    }

    // ===================== RESET — ALL =====================

    /// <summary>
    /// POST /Baseline/ResetAll — Nuclear option: restores everything from OG baseline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ResetAll()
    {
        using var admin = _db.Admin();
        using var mangos = _db.Mangos();

        int totalRestored = 0;
        var details = new List<object>();

        foreach (var (ogTable, sourceTable) in SNAPSHOT_TABLES)
        {
            if (!await TableExists(admin, ogTable))
                continue;

            var beforeCount = await mangos.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{sourceTable}`");

            await mangos.ExecuteAsync($"DELETE FROM `{sourceTable}`");
            await mangos.ExecuteAsync($"INSERT INTO `{sourceTable}` SELECT * FROM `vmangos_admin`.`{ogTable}`");

            var afterCount = await mangos.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{sourceTable}`");
            totalRestored += afterCount;

            details.Add(new { table = sourceTable, beforeCount, afterCount });
        }

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "baseline_reset_all",
            TargetType = "baseline",
            TargetName = "all_tables",
            StateAfter = JsonSerializer.Serialize(details),
            IsReversible = false,
            Success = true,
            Notes = $"Full baseline reset: all tables ({totalRestored} total rows restored)"
        });

        return Json(new { success = true, totalRestored, tables = details });
    }

    // ===================== HELPERS =====================

    private async Task<bool> TableExists(MySqlConnector.MySqlConnection conn, string tableName)
    {
        // Get the database name from the connection string
        var dbName = conn.Database;
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @Db AND table_name = @Table",
            new { Db = dbName, Table = tableName });
        return count > 0;
    }

    private List<object> BuildDiff(IDictionary<string, object> ogDict, IDictionary<string, object> curDict)
    {
        var changes = new List<object>();

        foreach (var key in ogDict.Keys)
        {
            if (!curDict.ContainsKey(key)) continue;

            var ogVal = ogDict[key];
            var curVal = curDict[key];

            // Normalize nulls
            var ogStr = ogVal?.ToString() ?? "";
            var curStr = curVal?.ToString() ?? "";

            if (ogStr != curStr)
            {
                changes.Add(new
                {
                    field = key,
                    original = ogStr,
                    current = curStr
                });
            }
        }

        return changes;
    }

    private async Task<List<object>> CompareLootTable(
        MySqlConnector.MySqlConnection admin,
        MySqlConnector.MySqlConnection mangos,
        string tableName,
        int entry)
    {
        var ogTableName = "og_" + tableName;
        var changes = new List<object>();

        // Get OG rows
        var ogRows = (await admin.QueryAsync<dynamic>(
            $"SELECT * FROM `{ogTableName}` WHERE entry = @Entry", new { Entry = entry })).ToList();

        // Get current rows
        var currentRows = (await mangos.QueryAsync<dynamic>(
            $"SELECT * FROM `{tableName}` WHERE entry = @Entry", new { Entry = entry })).ToList();

        // Build lookup keys: (item, groupid, patch_min, patch_max)
        string RowKey(dynamic row)
        {
            var dict = (IDictionary<string, object>)row;
            var item = dict.ContainsKey("item") ? dict["item"]?.ToString() : "";
            var gid = dict.ContainsKey("groupid") ? dict["groupid"]?.ToString() : "0";
            var pmin = dict.ContainsKey("patch_min") ? dict["patch_min"]?.ToString() : "0";
            var pmax = dict.ContainsKey("patch_max") ? dict["patch_max"]?.ToString() : "10";
            return $"{item}|{gid}|{pmin}|{pmax}";
        }

        var ogMap = new Dictionary<string, dynamic>();
        foreach (var r in ogRows) ogMap[RowKey(r)] = r;

        var curMap = new Dictionary<string, dynamic>();
        foreach (var r in currentRows) curMap[RowKey(r)] = r;

        // Find modified rows
        foreach (var kv in ogMap)
        {
            if (curMap.TryGetValue(kv.Key, out var curRow))
            {
                var ogDict = (IDictionary<string, object>)kv.Value;
                var curDict = (IDictionary<string, object>)curRow;

                // Check ChanceOrQuestChance specifically (most common edit)
                var ogChance = ogDict.ContainsKey("ChanceOrQuestChance") ? Convert.ToSingle(ogDict["ChanceOrQuestChance"]) : 0f;
                var curChance = curDict.ContainsKey("ChanceOrQuestChance") ? Convert.ToSingle(curDict["ChanceOrQuestChance"]) : 0f;

                var ogMaxCount = ogDict.ContainsKey("maxcount") ? Convert.ToInt32(ogDict["maxcount"]) : 0;
                var curMaxCount = curDict.ContainsKey("maxcount") ? Convert.ToInt32(curDict["maxcount"]) : 0;

                if (Math.Abs(ogChance - curChance) > 0.0001f || ogMaxCount != curMaxCount)
                {
                    // Get item name for display
                    var itemId = ogDict.ContainsKey("item") ? Convert.ToInt32(ogDict["item"]) : 0;

                    changes.Add(new
                    {
                        type = "modified",
                        item = itemId,
                        key = kv.Key,
                        ogChance,
                        curChance,
                        ogMaxCount,
                        curMaxCount
                    });
                }
            }
            else
            {
                // Row exists in OG but not in current — deleted
                var ogDict = (IDictionary<string, object>)kv.Value;
                changes.Add(new
                {
                    type = "deleted",
                    item = ogDict.ContainsKey("item") ? Convert.ToInt32(ogDict["item"]) : 0,
                    key = kv.Key
                });
            }
        }

        // Find added rows (in current but not in OG)
        foreach (var kv in curMap)
        {
            if (!ogMap.ContainsKey(kv.Key))
            {
                var curDict = (IDictionary<string, object>)kv.Value;
                changes.Add(new
                {
                    type = "added",
                    item = curDict.ContainsKey("item") ? Convert.ToInt32(curDict["item"]) : 0,
                    key = kv.Key
                });
            }
        }

        return changes;
    }

    private async Task<int> ResetLootTableEntry(
        MySqlConnector.MySqlConnection admin,
        MySqlConnector.MySqlConnection mangos,
        string tableName,
        int entry)
    {
        var ogTableName = "og_" + tableName;

        // Get OG rows
        var ogRows = (await admin.QueryAsync<dynamic>(
            $"SELECT * FROM `{ogTableName}` WHERE entry = @Entry", new { Entry = entry })).ToList();

        if (ogRows.Count == 0) return 0;

        // Delete current rows
        await mangos.ExecuteAsync($"DELETE FROM `{tableName}` WHERE entry = @Entry", new { Entry = entry });

        // Re-insert OG rows
        foreach (var ogRow in ogRows)
        {
            var dict = (IDictionary<string, object>)ogRow;
            var columns = string.Join(", ", dict.Keys.Select(k => $"`{k}`"));
            var paramNames = string.Join(", ", dict.Keys.Select(k => $"@{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in dict)
                parameters.Add(kv.Key, kv.Value);
            await mangos.ExecuteAsync($"INSERT INTO `{tableName}` ({columns}) VALUES ({paramNames})", parameters);
        }

        return ogRows.Count;
    }
}