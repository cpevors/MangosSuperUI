using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class SpellsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;

    public SpellsController(ConnectionFactory db, DbcService dbc, AuditService audit)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
    }

    public IActionResult Index() => View();

    // ===================== SEARCH =====================

    [HttpGet]
    public async Task<IActionResult> Search(string? q, int? schoolFilter, int? mechanicFilter,
        int page = 1, int pageSize = 50)
    {
        using var conn = _db.Mangos();

        var where = "WHERE build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(q))
        {
            if (uint.TryParse(q.Trim(), out var spellId))
            {
                where += " AND entry = @SpellId";
                parameters.Add("SpellId", spellId);
            }
            else
            {
                where += " AND name LIKE @Search";
                parameters.Add("Search", $"%{q.Trim()}%");
            }
        }

        if (schoolFilter.HasValue)
        {
            where += " AND school = @School";
            parameters.Add("School", schoolFilter.Value);
        }

        if (mechanicFilter.HasValue)
        {
            where += " AND mechanic = @Mechanic";
            parameters.Add("Mechanic", mechanicFilter.Value);
        }

        var countSql = $"SELECT COUNT(*) FROM spell_template {where}";
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        var offset = (page - 1) * pageSize;
        parameters.Add("Offset", offset);
        parameters.Add("PageSize", pageSize);

        var dataSql = $@"
            SELECT entry, name, nameSubtext, school, mechanic,
                   spellIconId, castingTimeIndex, durationIndex, rangeIndex,
                   manaCost, spellLevel, baseLevel, maxLevel,
                   effect1, effect2, effect3,
                   effectApplyAuraName1, effectApplyAuraName2, effectApplyAuraName3,
                   effectBasePoints1, effectBasePoints2, effectBasePoints3,
                   effectDieSides1, effectDieSides2, effectDieSides3,
                   effectTriggerSpell1, effectTriggerSpell2, effectTriggerSpell3,
                   effectMiscValue1, effectMiscValue2, effectMiscValue3,
                   procChance, procFlags, procCharges,
                   attributes, attributesEx, attributesEx2, attributesEx3, attributesEx4,
                   recoveryTime, categoryRecoveryTime
            FROM spell_template {where}
            ORDER BY name ASC, spellLevel ASC, entry ASC
            LIMIT @PageSize OFFSET @Offset";

        var spells = (await conn.QueryAsync<dynamic>(dataSql, parameters)).ToList();

        var iconMap = new Dictionary<uint, string>();
        foreach (var spell in spells)
        {
            uint iconId = (uint)(spell.spellIconId ?? 0);
            if (iconId > 0 && !iconMap.ContainsKey(iconId))
                iconMap[iconId] = _dbc.GetSpellIconPath(iconId);
        }

        return Json(new
        {
            spells,
            icons = iconMap,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    // ===================== DETAIL =====================

    [HttpGet]
    public async Task<IActionResult> Detail(int entry)
    {
        using var conn = _db.Mangos();

        var spell = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
            new { Entry = entry });

        if (spell == null)
            return Json(new { found = false });

        uint iconId = (uint)(spell.spellIconId ?? 0);
        var iconPath = _dbc.GetSpellIconPath(iconId);

        uint durationIdx = (uint)(spell.durationIndex ?? 0);
        uint castTimeIdx = (uint)(spell.castingTimeIndex ?? 0);
        uint rangeIdx = (uint)(spell.rangeIndex ?? 0);

        var durationInfo = _dbc.SpellDurations.TryGetValue(durationIdx, out var d) ? d : null;
        var castTimeInfo = _dbc.SpellCastTimes.TryGetValue(castTimeIdx, out var c) ? c : null;
        var rangeInfo = _dbc.SpellRanges.TryGetValue(rangeIdx, out var r) ? r : null;

        // OG baseline diff
        object? ogDiff = null;
        try
        {
            using var adminConn = _db.Admin();
            var ogExists = await adminConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM og_baseline_meta WHERE table_name = 'og_spell_template'");

            if (ogExists > 0)
            {
                var ogSpell = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    @"SELECT * FROM vmangos_admin.og_spell_template
                      WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
                    new { Entry = entry });

                if (ogSpell != null)
                {
                    var curDict = (IDictionary<string, object>)spell;
                    var ogDict = (IDictionary<string, object>)ogSpell;
                    var diffs = new Dictionary<string, object>();

                    foreach (var key in ogDict.Keys)
                    {
                        if (key == "entry" || key == "build") continue;
                        if (!curDict.ContainsKey(key)) continue;
                        if (!Equals(ogDict[key], curDict[key]))
                            diffs[key] = new { og = ogDict[key], cur = curDict[key] };
                    }

                    if (diffs.Count > 0)
                        ogDiff = diffs;
                }
            }
        }
        catch { /* OG table may not exist yet */ }

        return Json(new
        {
            found = true,
            spell,
            iconPath,
            durationLabel = durationInfo?.DisplayLabel ?? "Unknown",
            castTimeLabel = castTimeInfo?.DisplayLabel ?? "Unknown",
            rangeLabel = rangeInfo?.DisplayLabel ?? "Unknown",
            ogDiff
        });
    }

    // ===================== DBC METADATA =====================

    [HttpGet]
    public IActionResult DbcMeta()
    {
        var castTimes = _dbc.SpellCastTimes
            .Select(kv => new { id = kv.Key, label = kv.Value.DisplayLabel })
            .OrderBy(c => c.id);
        var durations = _dbc.SpellDurations
            .Select(kv => new { id = kv.Key, label = kv.Value.DisplayLabel })
            .OrderBy(d => d.id);
        var ranges = _dbc.SpellRanges
            .Select(kv => new { id = kv.Key, label = kv.Value.DisplayLabel })
            .OrderBy(r => r.id);

        return Json(new { castTimes, durations, ranges });
    }

    // ===================== COLUMNS =====================

    [HttpGet]
    public async Task<IActionResult> Columns()
    {
        using var conn = _db.Mangos();
        var columns = (await conn.QueryAsync<string>(
            @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'spell_template'
              ORDER BY ORDINAL_POSITION")).ToList();
        return Json(columns);
    }

    // ===================== SEARCH GROUPED =====================

    /// <summary>
    /// GET /Spells/SearchGrouped?q=fireball&schoolFilter=2&page=1&pageSize=50
    /// Groups spells by name+spellFamilyName. Returns collapsed rank groups.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> SearchGrouped(string? q, int? schoolFilter, int? mechanicFilter,
        int page = 1, int pageSize = 50)
    {
        using var conn = _db.Mangos();

        var where = "WHERE build = (SELECT MAX(build) FROM spell_template st2 WHERE st2.entry = spell_template.entry)";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(q))
        {
            if (uint.TryParse(q.Trim(), out var spellId))
            {
                where += " AND entry = @SpellId";
                parameters.Add("SpellId", spellId);
            }
            else
            {
                where += " AND name LIKE @Search";
                parameters.Add("Search", $"%{q.Trim()}%");
            }
        }

        if (schoolFilter.HasValue)
        {
            where += " AND school = @School";
            parameters.Add("School", schoolFilter.Value);
        }

        if (mechanicFilter.HasValue)
        {
            where += " AND mechanic = @Mechanic";
            parameters.Add("Mechanic", mechanicFilter.Value);
        }

        // Get ALL matching spells (no pagination yet — we paginate on groups)
        var allSql = $@"
            SELECT entry, name, nameSubtext, school, spellFamilyName,
                   spellIconId, spellLevel, manaCost,
                   effect1, effectBasePoints1, effectDieSides1
            FROM spell_template {where}
            ORDER BY name ASC, spellLevel ASC, entry ASC
            LIMIT 5000";

        var allSpells = (await conn.QueryAsync<dynamic>(allSql, parameters)).ToList();

        // Group by (name, spellFamilyName) — this separates player Fireball from NPC Fireball
        var groups = allSpells
            .GroupBy(s => new { name = (string)(s.name ?? ""), family = (int)(s.spellFamilyName ?? 0) })
            .Select(g =>
            {
                var spells = g.OrderBy(s => (int)(s.spellLevel ?? 0)).ThenBy(s => (int)s.entry).ToList();
                var first = spells.First();
                var levelMin = spells.Min(s => (int)(s.spellLevel ?? 0));
                var levelMax = spells.Max(s => (int)(s.spellLevel ?? 0));

                return new
                {
                    name = g.Key.name,
                    family = g.Key.family,
                    school = (int)(first.school ?? 0),
                    spellIconId = (uint)(first.spellIconId ?? 0),
                    rankCount = spells.Count,
                    levelRange = levelMin == levelMax ? $"{levelMin}" : $"{levelMin}-{levelMax}",
                    entries = spells.Select(s => (int)s.entry).ToList(),
                    firstEntry = (int)first.entry
                };
            })
            .OrderBy(g => g.name)
            .ToList();

        var totalGroups = groups.Count;
        var pagedGroups = groups.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Resolve icons
        var iconMap = new Dictionary<uint, string>();
        foreach (var g in pagedGroups)
        {
            if (g.spellIconId > 0 && !iconMap.ContainsKey(g.spellIconId))
                iconMap[g.spellIconId] = _dbc.GetSpellIconPath(g.spellIconId);
        }

        return Json(new
        {
            groups = pagedGroups,
            icons = iconMap,
            totalGroups,
            totalSpells = allSpells.Count,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalGroups / pageSize)
        });
    }

    // ===================== GROUP DETAIL =====================

    /// <summary>
    /// POST /Spells/GroupDetail — Returns full data for all spells in a rank group.
    /// Analyzes shared vs per-rank fields.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GroupDetail([FromBody] int[] entries)
    {
        if (entries == null || entries.Length == 0)
            return Json(new { found = false });

        using var conn = _db.Mangos();

        var spells = new List<IDictionary<string, object>>();
        var iconMap = new Dictionary<uint, string>();

        foreach (var entry in entries)
        {
            var spell = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
                new { Entry = entry });

            if (spell != null)
            {
                spells.Add((IDictionary<string, object>)spell);
                uint iconId = (uint)(((IDictionary<string, object>)spell).ContainsKey("spellIconId")
                    ? Convert.ToUInt32(((IDictionary<string, object>)spell)["spellIconId"] ?? 0) : 0);
                if (iconId > 0 && !iconMap.ContainsKey(iconId))
                    iconMap[iconId] = _dbc.GetSpellIconPath(iconId);
            }
        }

        if (spells.Count == 0)
            return Json(new { found = false });

        // Analyze shared vs per-rank fields
        var immutable = new HashSet<string> { "entry", "build" };
        var shared = new Dictionary<string, object?>();
        var perRank = new HashSet<string>();

        // Use first spell as reference
        var refSpell = spells[0];
        foreach (var key in refSpell.Keys)
        {
            if (immutable.Contains(key)) continue;

            bool allSame = spells.All(s =>
                s.ContainsKey(key) && Equals(s[key]?.ToString(), refSpell[key]?.ToString()));

            if (allSame)
                shared[key] = refSpell[key];
            else
                perRank.Add(key);
        }

        return Json(new
        {
            found = true,
            spells,
            icons = iconMap,
            sharedFields = shared.Keys.ToList(),
            perRankFields = perRank.ToList(),
            sharedValues = shared
        });
    }

    // ===================== SAVE BATCH =====================

    /// <summary>
    /// POST /Spells/SaveBatch — Applies the same changes to multiple spell entries.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SaveBatch([FromBody] SpellBatchSaveRequest request)
    {
        if (request.entries == null || request.entries.Length == 0)
            return Json(new { success = false, error = "No entries provided." });

        if (request.changes == null || request.changes.Count == 0)
            return Json(new { success = false, error = "No changes provided." });

        using var conn = _db.Mangos();
        var immutable = new HashSet<string> { "entry", "build" };
        int totalUpdated = 0;

        foreach (var entry in request.entries)
        {
            var currentSpell = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
                new { Entry = entry });

            if (currentSpell == null) continue;

            var currentDict = (IDictionary<string, object>)currentSpell;
            int build = Convert.ToInt32(currentDict["build"] ?? 0);

            var setClauses = new List<string>();
            var parameters = new DynamicParameters();

            foreach (var kvp in request.changes)
            {
                var col = kvp.Key;
                if (immutable.Contains(col)) continue;
                if (!currentDict.ContainsKey(col)) continue;

                setClauses.Add($"`{col}` = @p_{col}");

                if (kvp.Value is JsonElement je)
                {
                    switch (je.ValueKind)
                    {
                        case JsonValueKind.Number:
                            if (je.TryGetInt64(out var lv)) parameters.Add($"p_{col}", lv);
                            else if (je.TryGetDouble(out var dv)) parameters.Add($"p_{col}", dv);
                            else parameters.Add($"p_{col}", je.GetRawText());
                            break;
                        case JsonValueKind.String:
                            parameters.Add($"p_{col}", je.GetString());
                            break;
                        default:
                            parameters.Add($"p_{col}", kvp.Value);
                            break;
                    }
                }
                else
                {
                    parameters.Add($"p_{col}", kvp.Value);
                }
            }

            if (setClauses.Count == 0) continue;

            parameters.Add("Entry", entry);
            parameters.Add("Build", build);

            await conn.ExecuteAsync(
                $"UPDATE spell_template SET {string.Join(", ", setClauses)} WHERE entry = @Entry AND build = @Build",
                parameters);

            totalUpdated++;
        }

        // Audit
        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "spell_batch_edit",
            TargetType = "spell_template",
            TargetName = $"Batch: {request.entries.Length} spells",
            StateAfter = JsonSerializer.Serialize(new { entries = request.entries, changes = request.changes }),
            IsReversible = true,
            Success = totalUpdated > 0,
            Notes = $"Batch edit: {request.changes.Count} field(s) applied to {totalUpdated}/{request.entries.Length} spells. Restart required."
        });

        return Json(new
        {
            success = totalUpdated > 0,
            totalUpdated,
            restartRequired = true
        });
    }

    // ===================== SAVE =====================

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SpellSaveRequest request)
    {
        if (request.entry <= 0)
            return Json(new { success = false, error = "Invalid spell entry." });

        if (request.changes == null || request.changes.Count == 0)
            return Json(new { success = false, error = "No changes provided." });

        using var conn = _db.Mangos();

        var currentSpell = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM spell_template WHERE entry = @Entry ORDER BY build DESC LIMIT 1",
            new { Entry = request.entry });

        if (currentSpell == null)
            return Json(new { success = false, error = "Spell not found." });

        var currentDict = (IDictionary<string, object>)currentSpell;
        int build = Convert.ToInt32(currentDict["build"] ?? 0);

        var immutable = new HashSet<string> { "entry", "build" };
        var setClauses = new List<string>();
        var parameters = new DynamicParameters();
        var beforeState = new Dictionary<string, object?>();
        var afterState = new Dictionary<string, object?>();

        foreach (var kvp in request.changes)
        {
            var col = kvp.Key;
            if (immutable.Contains(col)) continue;
            if (!currentDict.ContainsKey(col)) continue;

            beforeState[col] = currentDict[col];
            afterState[col] = kvp.Value;
            setClauses.Add($"`{col}` = @p_{col}");

            if (kvp.Value is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (je.TryGetInt64(out var lv)) parameters.Add($"p_{col}", lv);
                        else if (je.TryGetDouble(out var dv)) parameters.Add($"p_{col}", dv);
                        else parameters.Add($"p_{col}", je.GetRawText());
                        break;
                    case JsonValueKind.String:
                        parameters.Add($"p_{col}", je.GetString());
                        break;
                    case JsonValueKind.Null:
                        parameters.Add($"p_{col}", (object?)null);
                        break;
                    default:
                        parameters.Add($"p_{col}", je.GetRawText());
                        break;
                }
            }
            else
            {
                parameters.Add($"p_{col}", kvp.Value);
            }
        }

        if (setClauses.Count == 0)
            return Json(new { success = false, error = "No valid changes after filtering." });

        parameters.Add("Entry", request.entry);
        parameters.Add("Build", build);

        var sql = $"UPDATE spell_template SET {string.Join(", ", setClauses)} WHERE entry = @Entry AND build = @Build";
        var affected = await conn.ExecuteAsync(sql, parameters);

        var spellName = currentDict.ContainsKey("name") ? currentDict["name"]?.ToString() : $"Spell #{request.entry}";
        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "spell_edit",
            TargetType = "spell_template",
            TargetName = $"{spellName} (#{request.entry})",
            StateBefore = JsonSerializer.Serialize(beforeState),
            StateAfter = JsonSerializer.Serialize(afterState),
            IsReversible = true,
            Success = affected > 0,
            Notes = $"Edited {setClauses.Count} field(s) on spell #{request.entry}. Server restart required."
        });

        return Json(new
        {
            success = affected > 0,
            fieldsUpdated = setClauses.Count,
            restartRequired = true
        });
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────

public class SpellSaveRequest
{
    public int entry { get; set; }
    public Dictionary<string, object?> changes { get; set; } = new();
}

public class SpellBatchSaveRequest
{
    public int[] entries { get; set; } = Array.Empty<int>();
    public Dictionary<string, object?> changes { get; set; } = new();
}