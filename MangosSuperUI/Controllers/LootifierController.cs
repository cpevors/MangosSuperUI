using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;
using MySqlConnector;

namespace MangosSuperUI.Controllers;

public class LootifierController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;

    private const int LOOTIFIER_ID_START = 950000;

    // ── VERIFIED stat type mapping (confirmed from VMaNGOS item_template) ──
    // Judgement Legplates: 5=27(Int), 6=5(Spi), 7=26(Sta), 4=10(Str) ✓
    // Eye of Rend: 4=13(Str), 7=7(Sta) ✓
    // Corsair's Overshirt: 6=11(Spi), 7=5(Sta) ✓
    private static readonly Dictionary<int, string> STAT_NAMES = new()
    {
        [0] = "None",
        [1] = "Health",
        [3] = "Agility",
        [4] = "Strength",
        [5] = "Intellect",    // VERIFIED: Judgement Legplates type5=27 = +27 Int
        [6] = "Spirit",       // VERIFIED: Corsair's Overshirt type6=11 = +11 Spi
        [7] = "Stamina"       // VERIFIED: Eye of Rend type7=7 = +7 Sta
    };

    // ── Stat families (5=Int, 6=Spi, 7=Sta) ──
    private static readonly Dictionary<string, HashSet<int>> STAT_FAMILIES = new()
    {
        ["physical"] = new HashSet<int> { 3, 4, 7 },       // Agi, Str, Sta
        ["caster"] = new HashSet<int> { 5, 6, 7 },         // Int, Spirit, Sta
        ["hybrid"] = new HashSet<int> { 3, 4, 5, 6, 7 },
    };

    // ── Verified stat budget weights (Blizzard StatMod values) ──
    // Stamina(7) = 2/3 cost. All others = 1.0.
    private static readonly Dictionary<int, float> DEFAULT_STAT_WEIGHTS = new()
    {
        [3] = 1.0f,      // Agility
        [4] = 1.0f,      // Strength
        [5] = 1.0f,      // Intellect
        [6] = 1.0f,      // Spirit
        [7] = 0.6667f    // Stamina (2/3 budget cost per point)
    };

    // ── Spell trigger types ──
    private const int SPELLTRIGGER_USE = 0;
    private const int SPELLTRIGGER_EQUIP = 1;
    private const int SPELLTRIGGER_CHANCE_ON_HIT = 2;

    // Map IDs for batch mode
    private static readonly Dictionary<int, string> MAP_NAMES = new()
    {
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
        [289] = "Scholomance",
        [309] = "Zul'Gurub",
        [329] = "Stratholme",
        [349] = "Maraudon",
        [389] = "Ragefire Chasm",
        [409] = "Molten Core",
        [429] = "Dire Maul",
        [469] = "Blackwing Lair",
        [509] = "Ruins of Ahn'Qiraj",
        [531] = "Temple of Ahn'Qiraj",
        [533] = "Naxxramas"
    };

    public LootifierController(ConnectionFactory db, DbcService dbc, AuditService audit, IWebHostEnvironment env)
    {
        _db = db;
        _dbc = dbc;
        _audit = audit;
        _env = env;
    }

    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Meta()
    {
        return Json(new
        {
            statNames = STAT_NAMES,
            defaultStatWeights = DEFAULT_STAT_WEIGHTS,
            defaultNamingTiers = new[]
            {
                new { minPct = 0, maxPct = 79, label = "Improved", position = "prefix" },
                new { minPct = 80, maxPct = 89, label = "of Power", position = "suffix" },
                new { minPct = 90, maxPct = 97, label = "of Glory", position = "suffix" },
                new { minPct = 98, maxPct = 100, label = "of the Gods", position = "suffix" }
            },
            defaultRuleset = new
            {
                budgetCeilingPct = 35,
                variantsPerItem = 10,
                allowNewAffixes = true,
                maxAffixCountChange = 1,
                dropChanceStrategy = "preserve"
            },
            maps = MAP_NAMES.OrderBy(kv => kv.Value).Select(kv => new { id = kv.Key, name = kv.Value })
        });
    }

    // ===================== CREATURE SEARCH =====================

    [HttpGet]
    public async Task<IActionResult> SearchCreature(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Json(new { results = Array.Empty<object>() });

        using var conn = _db.Mangos();
        var results = await conn.QueryAsync<dynamic>(@"
            SELECT ct.entry, ct.name, ct.rank, ct.level_min, ct.level_max, ct.loot_id
            FROM creature_template ct
            WHERE ct.name LIKE @Q
              AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)
              AND ct.loot_id > 0
            ORDER BY ct.rank DESC, ct.level_max DESC, ct.name
            LIMIT 25", new { Q = $"%{q}%" });

        return Json(new { results });
    }

    // ===================== LOOT TREE =====================

    [HttpGet]
    public async Task<IActionResult> LootTree(int creatureEntry)
    {
        using var conn = _db.Mangos();

        var creature = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT entry, name, rank, level_min, level_max, loot_id
            FROM creature_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
            new { E = creatureEntry });

        if (creature == null)
            return Json(new { success = false, error = "Creature not found" });

        int lootId = (int)creature.loot_id;

        var allRows = (await conn.QueryAsync<LootifierLootRow>(@"
            SELECT lt.entry AS lootEntry, lt.item, lt.ChanceOrQuestChance AS chance,
                   lt.groupid AS groupId, lt.mincountOrRef, lt.maxcount,
                   lt.patch_min AS patchMin, lt.patch_max AS patchMax
            FROM creature_loot_template lt
            WHERE lt.entry = @LootId
            ORDER BY lt.groupid, lt.mincountOrRef, lt.ChanceOrQuestChance DESC",
            new { LootId = lootId })).ToList();

        var directItems = allRows.Where(r => r.mincountOrRef > 0).ToList();
        var refPointers = allRows.Where(r => r.mincountOrRef < 0).ToList();

        var refGroups = new List<object>();
        foreach (var ptr in refPointers)
        {
            int refEntry = Math.Abs(ptr.mincountOrRef);
            var refItems = (await conn.QueryAsync<LootifierLootRow>(@"
                SELECT rlt.entry AS lootEntry, rlt.item, rlt.ChanceOrQuestChance AS chance,
                       rlt.groupid AS groupId, rlt.mincountOrRef, rlt.maxcount,
                       rlt.patch_min AS patchMin, rlt.patch_max AS patchMax
                FROM reference_loot_template rlt
                WHERE rlt.entry = @RefEntry
                ORDER BY rlt.groupid, rlt.ChanceOrQuestChance DESC",
                new { RefEntry = refEntry })).ToList();

            var enriched = await EnrichLootRows(conn, refItems);

            refGroups.Add(new
            {
                refEntry,
                pointerChance = ptr.chance,
                pointerGroupId = ptr.groupId,
                items = enriched
            });
        }

        var directEnriched = await EnrichLootRows(conn, directItems);

        var iconMap = new Dictionary<uint, string>();
        void addIcons(IEnumerable<dynamic> rows)
        {
            foreach (var r in rows)
            {
                uint did = (uint)(r.displayId ?? 0);
                if (did > 0 && !iconMap.ContainsKey(did))
                    iconMap[did] = _dbc.GetItemIconPath(did);
            }
        }
        addIcons(directEnriched);
        foreach (var rg in refGroups)
            addIcons(((dynamic)rg).items as IEnumerable<dynamic> ?? Enumerable.Empty<dynamic>());

        return Json(new
        {
            success = true,
            creature = new { creature.entry, creature.name, creature.rank, creature.level_min, creature.level_max, creature.loot_id },
            directItems = directEnriched,
            referenceGroups = refGroups,
            icons = iconMap
        });
    }

    // ===================== ANALYZE ITEM =====================

    [HttpGet]
    public async Task<IActionResult> AnalyzeItem(int entry)
    {
        using var conn = _db.Mangos();
        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM item_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
            new { E = entry });

        if (item == null)
            return Json(new { success = false, error = "Item not found" });

        return Json(new { success = true, analysis = AnalyzeItemStats(item) });
    }

    // ===================== GENERATE PREVIEW (single source) =====================

    [HttpPost]
    public async Task<IActionResult> GeneratePreview([FromBody] GenerateRequest request)
    {
        if (request.itemEntries == null || request.itemEntries.Length == 0)
            return Json(new { success = false, error = "No items selected" });

        using var conn = _db.Mangos();
        var ruleset = request.ruleset ?? new RulesetDto();
        var allVariants = new List<object>();

        foreach (var itemEntry in request.itemEntries)
        {
            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM item_template
                WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = itemEntry });

            if (item == null) continue;

            var analysis = AnalyzeItemStats(item);
            bool hasStats = (int)analysis.totalStats > 0;
            bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;

            // Skip items with neither stats nor spell effects
            if (!hasStats && !hasSpellEffects) continue;

            var variants = GenerateVariants(item, analysis, ruleset);
            allVariants.Add(new
            {
                baseItem = new
                {
                    entry = (int)item.entry,
                    name = (string)item.name,
                    quality = (int)item.quality,
                    displayId = (uint)item.display_id
                },
                analysis,
                variants = VariantsToJson(variants)
            });
        }

        // Generate legendary preview if enabled
        object? legendaryPreview = null;
        if (ruleset.generateLegendary && request.creatureEntry > 0)
        {
            var eligibleEntries = allVariants.Count > 0
                ? request.itemEntries.ToList()
                : new List<int>();
            legendaryPreview = await PreviewLegendary(conn, request.creatureEntry, eligibleEntries, ruleset);
        }

        return Json(new { success = true, items = allVariants, legendary = legendaryPreview });
    }

    // ===================== CURATED BOSS LIST =====================

    private List<int> GetCuratedBossEntries(int[]? mapIds)
    {
        var path = Path.Combine(_env.WebRootPath, "data", "instance-bosses.json");
        if (!System.IO.File.Exists(path)) return new List<int>();

        var json = System.IO.File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        var entries = new List<int>();

        foreach (var instance in doc.RootElement.GetProperty("instances").EnumerateArray())
        {
            int mapId = instance.GetProperty("mapId").GetInt32();
            if (mapIds != null && mapIds.Length > 0 && !mapIds.Contains(mapId))
                continue;

            foreach (var boss in instance.GetProperty("bosses").EnumerateArray())
                entries.Add(boss.GetProperty("entry").GetInt32());
        }

        return entries;
    }

    // ===================== BATCH PREVIEW =====================

    [HttpPost]
    public async Task<IActionResult> BatchPreview([FromBody] BatchRequest request)
    {
        using var conn = _db.Mangos();

        string ItemQualityWhere(DynamicParameters p, string prefix)
        {
            if (request.qualities != null && request.qualities.Length > 0)
            {
                p.Add(prefix + "Qualities", request.qualities);
                return $"it.quality IN @{prefix}Qualities";
            }
            return "";
        }
        string ItemLevelWhere(DynamicParameters p, string prefix)
        {
            var parts = new List<string>();
            if (request.levelMin > 0) { p.Add(prefix + "LevelMin", request.levelMin); parts.Add($"it.required_level >= @{prefix}LevelMin"); }
            if (request.levelMax > 0) { p.Add(prefix + "LevelMax", request.levelMax); parts.Add($"it.required_level <= @{prefix}LevelMax"); }
            return string.Join(" AND ", parts);
        }
        // CHANGED: also include items with spell effects (no stat requirement)
        string ItemFilter() => "(it.stat_type1 > 0 OR it.stat_type2 > 0 OR it.stat_type3 > 0 OR it.spellid_1 > 0)";

        var p1 = new DynamicParameters();
        var w1 = new List<string> { "lt.mincountOrRef > 0", ItemFilter() };
        var j1 = new List<string>
        {
            @"JOIN item_template it ON it.entry = lt.item
              AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)",
            @"JOIN creature_template ct ON ct.loot_id = lt.entry
              AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)"
        };

        var qw = ItemQualityWhere(p1, "d");
        if (!string.IsNullOrEmpty(qw)) w1.Add(qw);
        var lw = ItemLevelWhere(p1, "d");
        if (!string.IsNullOrEmpty(lw)) w1.Add(lw);

        var hasBossRank = request.creatureRanks != null && request.creatureRanks.Contains(3);
        var curatedBosses = hasBossRank ? GetCuratedBossEntries(request.mapIds) : new List<int>();

        if (request.creatureRanks != null && request.creatureRanks.Length > 0)
        {
            if (curatedBosses.Count > 0)
            {
                w1.Add("(ct.rank IN @dRanks OR ct.entry IN @dBossEntries)");
                p1.Add("dRanks", request.creatureRanks);
                p1.Add("dBossEntries", curatedBosses);
            }
            else
            {
                w1.Add("ct.rank IN @dRanks");
                p1.Add("dRanks", request.creatureRanks);
            }
        }
        if (request.mapIds != null && request.mapIds.Length > 0)
        {
            j1.Add("JOIN creature c ON c.id = ct.entry");
            w1.Add("c.map IN @dMapIds");
            p1.Add("dMapIds", request.mapIds);
        }

        var directSql = $@"SELECT DISTINCT
                it.entry AS itemEntry, it.name AS itemName, it.quality, it.display_id AS displayId,
                it.required_level, it.item_level,
                ct.entry AS creatureEntry, ct.name AS creatureName, ct.rank AS creatureRank,
                ct.level_min, ct.level_max, ct.loot_id AS lootId,
                lt.ChanceOrQuestChance AS chance, lt.groupid
            FROM creature_loot_template lt
            {string.Join(" ", j1)}
            WHERE {string.Join(" AND ", w1)}";

        var directRows = (await conn.QueryAsync<dynamic>(directSql, p1)).ToList();

        var p2 = new DynamicParameters();
        var w2 = new List<string> { "rlt.mincountOrRef > 0", "clt.mincountOrRef < 0", ItemFilter() };
        var j2 = @"JOIN item_template it ON it.entry = rlt.item
                   AND it.patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = it.entry)
                   JOIN creature_template ct ON ct.loot_id = clt.entry
                   AND ct.patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = ct.entry)";

        var qw2 = ItemQualityWhere(p2, "r");
        if (!string.IsNullOrEmpty(qw2)) w2.Add(qw2);
        var lw2 = ItemLevelWhere(p2, "r");
        if (!string.IsNullOrEmpty(lw2)) w2.Add(lw2);

        if (request.creatureRanks != null && request.creatureRanks.Length > 0)
        {
            if (curatedBosses.Count > 0)
            {
                w2.Add("(ct.rank IN @rRanks OR ct.entry IN @rBossEntries)");
                p2.Add("rRanks", request.creatureRanks);
                p2.Add("rBossEntries", curatedBosses);
            }
            else
            {
                w2.Add("ct.rank IN @rRanks");
                p2.Add("rRanks", request.creatureRanks);
            }
        }

        string mapJoin2 = "";
        if (request.mapIds != null && request.mapIds.Length > 0)
        {
            mapJoin2 = "JOIN creature c ON c.id = ct.entry";
            w2.Add("c.map IN @rMapIds");
            p2.Add("rMapIds", request.mapIds);
        }

        var refSql = $@"SELECT DISTINCT
                it.entry AS itemEntry, it.name AS itemName, it.quality, it.display_id AS displayId,
                it.required_level, it.item_level,
                ct.entry AS creatureEntry, ct.name AS creatureName, ct.rank AS creatureRank,
                ct.level_min, ct.level_max, ct.loot_id AS lootId,
                rlt.ChanceOrQuestChance AS chance, rlt.groupid
            FROM creature_loot_template clt
            JOIN reference_loot_template rlt ON rlt.entry = ABS(clt.mincountOrRef)
            {j2}
            {mapJoin2}
            WHERE {string.Join(" AND ", w2)}";

        var refRows = (await conn.QueryAsync<dynamic>(refSql, p2)).ToList();

        var rows = directRows.Concat(refRows).ToList();

        var byCreature = rows.GroupBy(r => (int)r.creatureEntry).Select(g =>
        {
            var first = g.First();
            return new
            {
                creatureEntry = (int)first.creatureEntry,
                creatureName = (string)first.creatureName,
                creatureRank = (int)first.creatureRank,
                levelMin = (int)first.level_min,
                levelMax = (int)first.level_max,
                lootId = (int)first.lootId,
                items = g.Select(r => new
                {
                    itemEntry = (int)r.itemEntry,
                    itemName = (string)r.itemName,
                    quality = (int)r.quality,
                    displayId = (uint)r.displayId,
                    requiredLevel = (int)r.required_level,
                    chance = (float)r.chance
                }).DistinctBy(x => x.itemEntry).ToList()
            };
        }).ToList();

        var iconMap = new Dictionary<uint, string>();
        foreach (var r in rows)
        {
            uint did = (uint)r.displayId;
            if (did > 0 && !iconMap.ContainsKey(did))
                iconMap[did] = _dbc.GetItemIconPath(did);
        }

        return Json(new
        {
            success = true,
            totalItems = rows.Select(r => (int)r.itemEntry).Distinct().Count(),
            totalCreatures = byCreature.Count,
            creatures = byCreature,
            icons = iconMap,
            truncated = false
        });
    }

    // ===================== BATCH SAMPLE PREVIEW =====================

    /// <summary>
    /// POST /Lootifier/BatchSamplePreview — Pick representative items from the batch scan
    /// and generate full variant previews for them so the user can see what rolls look like.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> BatchSamplePreview([FromBody] BatchSampleRequest request)
    {
        if (request.itemEntries == null || request.itemEntries.Length == 0)
            return Json(new { success = false, error = "No items provided" });

        using var conn = _db.Mangos();
        var ruleset = request.ruleset ?? new RulesetDto();
        var sampleResults = new List<object>();

        foreach (var itemEntry in request.itemEntries)
        {
            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM item_template
                WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = itemEntry });

            if (item == null) continue;

            var analysis = AnalyzeItemStats(item);
            bool hasStats = (int)analysis.totalStats > 0;
            bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
            if (!hasStats && !hasSpellEffects) continue;

            var variants = GenerateVariants(item, analysis, ruleset);

            uint displayId = (uint)item.display_id;
            string iconPath = _dbc.GetItemIconPath(displayId);

            sampleResults.Add(new
            {
                baseItem = new
                {
                    entry = (int)item.entry,
                    name = (string)item.name,
                    quality = (int)item.quality,
                    displayId,
                    iconPath
                },
                analysis,
                variants = VariantsToJson(variants)
            });
        }

        // Generate legendary preview if enabled
        object? legendaryPreview = null;
        if ((request.ruleset?.generateLegendary ?? false) && request.creatureEntry > 0)
        {
            legendaryPreview = await PreviewLegendary(conn, request.creatureEntry, request.itemEntries.ToList(), request.ruleset!);
        }

        return Json(new { success = true, items = sampleResults, legendary = legendaryPreview });
    }

    // ===================== BATCH COMMIT (optimized with transactions + cached schema) =====================

    // Cached column list — fetched once per app lifetime
    private static List<string>? _cachedItemColumns;
    private static readonly SemaphoreSlim _columnCacheLock = new(1, 1);

    private async Task<List<string>> GetItemColumns(MySqlConnector.MySqlConnection conn)
    {
        if (_cachedItemColumns != null) return _cachedItemColumns;
        await _columnCacheLock.WaitAsync();
        try
        {
            if (_cachedItemColumns != null) return _cachedItemColumns;
            _cachedItemColumns = (await conn.QueryAsync<string>(
                "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'mangos' AND TABLE_NAME = 'item_template' ORDER BY ORDINAL_POSITION"
            )).ToList();
            return _cachedItemColumns;
        }
        finally { _columnCacheLock.Release(); }
    }

    [HttpPost]
    public async Task<IActionResult> BatchCommit([FromBody] BatchCommitRequest request)
    {
        using var mangosConn = _db.Mangos();
        using var adminConn = _db.Admin();

        await EnsureTrackingTables(adminConn);

        // Pre-cache column list
        var columns = await GetItemColumns(mangosConn);

        var ruleset = request.ruleset ?? new RulesetDto();
        int totalItemsCreated = 0;
        int totalLootRowsCreated = 0;
        int creaturesProcessed = 0;

        // Batch tracking rows for bulk insert
        var trackingItemRows = new List<(int genEntry, int baseEntry, int creatureEntry, float budgetPct, string tierName)>();
        var trackingLootRows = new List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>();

        try
        {
            foreach (var creatureGroup in request.creatures)
            {
                int lootId = await mangosConn.ExecuteScalarAsync<int>(@"
                    SELECT loot_id FROM creature_template
                    WHERE entry = @E ORDER BY patch DESC LIMIT 1",
                    new { E = creatureGroup.creatureEntry });

                if (lootId == 0) continue;

                int nextId = await GetNextLootifierId(adminConn);

                foreach (var itemEntry in creatureGroup.itemEntries)
                {
                    var item = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT * FROM item_template
                        WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                        new { E = itemEntry });

                    if (item == null) continue;

                    var analysis = AnalyzeItemStats(item);
                    bool hasStats = (int)analysis.totalStats > 0;
                    bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
                    if (!hasStats && !hasSpellEffects) continue;

                    var variants = GenerateVariants(item, analysis, ruleset);
                    var createdEntries = new List<int>();

                    foreach (var variant in variants)
                    {
                        int newEntry = nextId++;
                        var roll = VariantToCommitRoll(variant);

                        await InsertVariantItemFast(mangosConn, columns, item, newEntry, roll);

                        trackingItemRows.Add((newEntry, itemEntry, creatureGroup.creatureEntry, roll.budgetPct, roll.tierLabel ?? ""));
                        createdEntries.Add(newEntry);
                        totalItemsCreated++;
                    }

                    var commitRolls = new CommitRoll[variants.Count];
                    for (int ri = 0; ri < variants.Count; ri++)
                        commitRolls[ri] = VariantToCommitRoll(variants[ri]);

                    int lootRows = await ExpandLootTableFast(mangosConn, trackingLootRows, lootId,
                        itemEntry, createdEntries, commitRolls, creatureGroup.creatureEntry);
                    totalLootRowsCreated += lootRows;
                }

                creaturesProcessed++;

                // Generate legendary if enabled (one per creature, random item)
                if (ruleset.generateLegendary)
                {
                    try
                    {
                        var columns2 = await GetItemColumns(mangosConn);
                        int legendaryCreated = await GenerateAndInsertLegendary(
                            mangosConn, adminConn, columns2,
                            creatureGroup.creatureEntry, lootId,
                            creatureGroup.itemEntries.ToList(), ruleset,
                            trackingItemRows, trackingLootRows);
                        totalItemsCreated += legendaryCreated;
                        totalLootRowsCreated += legendaryCreated;
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail the whole batch for a legendary issue
                        System.Diagnostics.Debug.WriteLine($"Legendary generation failed for creature {creatureGroup.creatureEntry}: {ex.Message}");
                    }
                }

                // Flush tracking rows in batches of 500 to avoid huge SQL strings
                if (trackingItemRows.Count >= 500)
                {
                    await FlushTrackingItems(adminConn, trackingItemRows);
                    trackingItemRows.Clear();
                }
                if (trackingLootRows.Count >= 500)
                {
                    await FlushTrackingLoot(adminConn, trackingLootRows);
                    trackingLootRows.Clear();
                }
            }

            // Flush remaining
            if (trackingItemRows.Count > 0) await FlushTrackingItems(adminConn, trackingItemRows);
            if (trackingLootRows.Count > 0) await FlushTrackingLoot(adminConn, trackingLootRows);
        }
        catch
        {
            throw;
        }

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "lootifier_batch_commit",
            TargetType = "lootifier",
            TargetName = $"batch:{creaturesProcessed} creatures",
            StateBefore = "{}",
            StateAfter = JsonSerializer.Serialize(new { totalItemsCreated, totalLootRowsCreated, creaturesProcessed }),
            IsReversible = true,
            Success = true,
            Notes = $"Lootifier batch: {totalItemsCreated} variants + {totalLootRowsCreated} loot rows across {creaturesProcessed} creatures"
        });

        return Json(new { success = true, totalItemsCreated, totalLootRowsCreated, creaturesProcessed });
    }

    /// <summary>Bulk insert tracking items using multi-value INSERT.</summary>
    private async Task FlushTrackingItems(MySqlConnector.MySqlConnection adminConn,
        List<(int genEntry, int baseEntry, int creatureEntry, float budgetPct, string tierName)> rows)
    {
        if (rows.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO lootifier_generated_items (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at) VALUES ");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rows[i];
            sb.Append($"({r.genEntry},{r.baseEntry},{r.creatureEntry},{r.budgetPct:F2},'{MySqlHelper.EscapeString(r.tierName)}',NOW())");
        }
        await adminConn.ExecuteAsync(sb.ToString());
    }

    /// <summary>Bulk insert tracking loot entries using multi-value INSERT.</summary>
    private async Task FlushTrackingLoot(MySqlConnector.MySqlConnection adminConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)> rows)
    {
        if (rows.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO lootifier_loot_entries (creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance, created_at) VALUES ");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rows[i];
            sb.Append($"({r.creatureEntry},'{MySqlHelper.EscapeString(r.table)}',{r.lootEntry},{r.itemEntry},'{r.action}',{r.origChance:F4},{r.newChance:F4},NOW())");
        }
        await adminConn.ExecuteAsync(sb.ToString());
    }

    // ===================== COMMIT (single source) =====================

    [HttpPost]
    public async Task<IActionResult> Commit([FromBody] CommitRequest request)
    {
        if (request.creatureEntry <= 0)
            return Json(new { success = false, error = "Invalid creature entry" });

        using var mangosConn = _db.Mangos();
        using var adminConn = _db.Admin();

        await EnsureTrackingTables(adminConn);

        int lootId = await mangosConn.ExecuteScalarAsync<int>(@"
            SELECT loot_id FROM creature_template
            WHERE entry = @E ORDER BY patch DESC LIMIT 1",
            new { E = request.creatureEntry });

        if (lootId == 0)
            return Json(new { success = false, error = "Creature has no loot table" });

        int nextId = await GetNextLootifierId(adminConn);
        int totalItemsCreated = 0;
        int totalLootRowsCreated = 0;
        var commitLog = new List<object>();
        var ruleset = request.ruleset ?? new RulesetDto();

        foreach (var itemGroup in request.variants)
        {
            if (itemGroup.rolls == null || itemGroup.rolls.Length == 0) continue;

            var baseItem = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT * FROM item_template
                WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = itemGroup.baseItemEntry });

            if (baseItem == null) continue;

            var createdEntries = new List<int>();

            foreach (var roll in itemGroup.rolls)
            {
                int newEntry = nextId++;
                await InsertVariantItem(mangosConn, baseItem, newEntry, roll);

                await adminConn.ExecuteAsync(@"
                    INSERT INTO lootifier_generated_items
                        (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at)
                    VALUES (@GenEntry, @BaseEntry, @CreatureEntry, @BudgetPct, @TierName, NOW())",
                    new
                    {
                        GenEntry = newEntry,
                        BaseEntry = itemGroup.baseItemEntry,
                        CreatureEntry = request.creatureEntry,
                        BudgetPct = roll.budgetPct,
                        TierName = roll.tierLabel ?? ""
                    });

                createdEntries.Add(newEntry);
                totalItemsCreated++;
            }

            int lootRowsAdded = await ExpandLootTable(mangosConn, adminConn, lootId,
                itemGroup.baseItemEntry, createdEntries, itemGroup.rolls, request.creatureEntry);
            totalLootRowsCreated += lootRowsAdded;

            commitLog.Add(new
            {
                baseItem = itemGroup.baseItemEntry,
                baseName = (string)baseItem.name,
                variantsCreated = createdEntries.Count,
                lootRowsAdded
            });
        }

        // Generate legendary if enabled (single mode: user picks which item via legendaryItemEntry)
        if (ruleset.generateLegendary)
        {
            try
            {
                var allItemEntries = request.variants.Select(v => v.baseItemEntry).ToList();
                int legendaryCreated = await GenerateAndInsertLegendary(
                    mangosConn, adminConn, null,
                    request.creatureEntry, lootId,
                    allItemEntries, ruleset);
                totalItemsCreated += legendaryCreated;
                totalLootRowsCreated += legendaryCreated;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Legendary generation failed for creature {request.creatureEntry}: {ex.Message}");
            }
        }

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "lootifier_commit",
            TargetType = "lootifier",
            TargetName = $"creature:{request.creatureEntry}",
            StateBefore = "{}",
            StateAfter = JsonSerializer.Serialize(new { totalItemsCreated, totalLootRowsCreated, commitLog }),
            IsReversible = true,
            Success = true,
            Notes = $"Lootifier: {totalItemsCreated} variants + {totalLootRowsCreated} loot rows for creature {request.creatureEntry}"
        });

        return Json(new { success = true, totalItemsCreated, totalLootRowsCreated, details = commitLog });
    }

    // ===================== ROLLBACK =====================

    [HttpPost]
    public async Task<IActionResult> Rollback([FromBody] RollbackRequest request)
    {
        using var mangosConn = _db.Mangos();
        using var adminConn = _db.Admin();

        if (!await TableExists(adminConn, "lootifier_generated_items"))
            return Json(new { success = false, error = "No lootifier data found" });

        string where = request.creatureEntry > 0 ? "WHERE creature_entry = @CE" : "WHERE 1=1";

        var generatedItems = (await adminConn.QueryAsync<dynamic>(
            $"SELECT generated_entry, base_entry, creature_entry FROM lootifier_generated_items {where}",
            new { CE = request.creatureEntry })).ToList();

        var lootEntries = (await adminConn.QueryAsync<dynamic>(
            $"SELECT id, loot_table, loot_entry, item_entry, action_type, original_chance FROM lootifier_loot_entries {where}",
            new { CE = request.creatureEntry })).ToList();

        int itemsRemoved = 0, lootRowsFixed = 0;

        foreach (var gi in generatedItems)
        {
            await mangosConn.ExecuteAsync("DELETE FROM item_template WHERE entry = @E",
                new { E = (int)gi.generated_entry });
            itemsRemoved++;
        }

        foreach (var le in lootEntries)
        {
            string table = (string)le.loot_table;
            string action = (string)le.action_type;

            if (action == "inserted")
            {
                await mangosConn.ExecuteAsync(
                    $"DELETE FROM `{table}` WHERE entry = @Entry AND item = @Item",
                    new { Entry = (int)le.loot_entry, Item = (int)le.item_entry });
            }
            else if (action == "modified")
            {
                await mangosConn.ExecuteAsync(
                    $"UPDATE `{table}` SET ChanceOrQuestChance = @Chance WHERE entry = @Entry AND item = @Item",
                    new { Chance = (float)le.original_chance, Entry = (int)le.loot_entry, Item = (int)le.item_entry });
            }
            lootRowsFixed++;
        }

        await adminConn.ExecuteAsync($"DELETE FROM lootifier_generated_items {where}", new { CE = request.creatureEntry });
        await adminConn.ExecuteAsync($"DELETE FROM lootifier_loot_entries {where}", new { CE = request.creatureEntry });

        await _audit.LogAsync(new AuditEntry
        {
            Operator = "admin",
            OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            Category = "content",
            Action = "lootifier_rollback",
            TargetType = "lootifier",
            TargetName = request.creatureEntry > 0 ? $"creature:{request.creatureEntry}" : "all",
            StateBefore = JsonSerializer.Serialize(new { itemsRemoved, lootRowsFixed }),
            StateAfter = "{}",
            IsReversible = false,
            Success = true,
            Notes = $"Lootifier rollback: {itemsRemoved} items removed, {lootRowsFixed} loot entries restored"
        });

        return Json(new { success = true, itemsRemoved, lootRowsFixed });
    }

    // ===================== STATUS =====================

    [HttpGet]
    public async Task<IActionResult> Status()
    {
        using var adminConn = _db.Admin();

        if (!await TableExists(adminConn, "lootifier_generated_items"))
            return Json(new { active = false, totalItems = 0, creatures = Array.Empty<object>() });

        var totalItems = await adminConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM lootifier_generated_items");
        var creatures = await adminConn.QueryAsync<dynamic>(@"
            SELECT creature_entry AS creatureEntry, COUNT(*) AS variantCount, MIN(created_at) AS firstCreated
            FROM lootifier_generated_items GROUP BY creature_entry ORDER BY creature_entry");

        return Json(new { active = totalItems > 0, totalItems, creatures });
    }

    // ══════════════════════════════════════════════════════════════
    //  VARIANT GENERATION ENGINE (v3 — tier-quota + prefix/suffix + spell-effect items)
    // ══════════════════════════════════════════════════════════════

    private List<VariantData> GenerateVariants(dynamic baseItem, dynamic analysis, RulesetDto ruleset)
    {
        var rng = new Random();

        float baseBudget = (float)analysis.weightedBudget;
        int[] presentTypes = (int[])analysis.presentStatTypes;
        string family = (string)analysis.detectedFamily;
        bool hasStats = (int)analysis.totalStats > 0;
        bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;

        int numVariants = Math.Clamp(ruleset.variantsPerItem, 1, 50);
        var tiers = GetRequiredTiers(ruleset);

        // For spell-effect-only items with no stats, derive a synthetic budget from item_level
        if (!hasStats && hasSpellEffects)
        {
            int itemLevel = GetPropInt(baseItem, "item_level");
            baseBudget = EstimateBudgetFromItemLevel(itemLevel);
        }

        float maxBudget = baseBudget * (1 + ruleset.budgetCeilingPct / 100f);

        // ── Phase 1: Allocate variant slots per tier (proportional to tier width) ──
        var tierAllocations = AllocateTierSlots(tiers, numVariants);

        // ── Phase 2: Generate variants per tier ──
        var eligible = new HashSet<int>(presentTypes);
        if (ruleset.allowNewAffixes)
        {
            var familyStats = STAT_FAMILIES.GetValueOrDefault(family, STAT_FAMILIES["hybrid"]);
            foreach (var s in familyStats) eligible.Add(s);
        }
        var eligibleList = eligible.ToList();

        // For spell-only items, seed eligible with family-appropriate stats
        if (!hasStats && hasSpellEffects)
        {
            eligibleList = STAT_FAMILIES["hybrid"].ToList();
        }

        var baseFingerprint = hasStats ? BuildFingerprint(analysis) : "";
        var fingerprints = new HashSet<string>();
        fingerprints.Add(baseFingerprint);

        var variants = new List<VariantData>();

        for (int tierIdx = 0; tierIdx < tiers.Count; tierIdx++)
        {
            var tier = tiers[tierIdx];
            int slotsForTier = tierAllocations[tierIdx];

            for (int s = 0; s < slotsForTier; s++)
            {
                // Roll budget within this tier's range
                float tierMinBudget = maxBudget * (tier.minPct / 100f);
                float tierMaxBudget = maxBudget * (Math.Min(tier.maxPct, 100f) / 100f);
                float budgetRoll = tierMinBudget + (float)rng.NextDouble() * (tierMaxBudget - tierMinBudget);
                float budgetPct = maxBudget > 0 ? (budgetRoll / maxBudget) * 100f : 0;

                List<StatRoll> stats;
                if (hasStats)
                {
                    stats = RollStats(rng, budgetRoll, presentTypes, eligibleList, analysis, ruleset);
                }
                else
                {
                    // Spell-effect-only item: add bonus stats based on tier budget
                    stats = RollStatsForSpellItem(rng, budgetRoll, eligibleList, family);
                }

                string tierLabel = tier.label;
                string tierPosition = tier.position;
                string baseName = (string)baseItem.name;
                string name = ApplyTierName(baseName, tierLabel, tierPosition);

                var candidate = new VariantData
                {
                    name = name,
                    budgetPct = budgetPct,
                    tierLabel = tierLabel,
                    tierPosition = tierPosition,
                    stats = stats
                };

                var fp = BuildVariantFingerprint(candidate);
                if (fingerprints.Contains(fp))
                {
                    // Retry within same tier (up to 10 attempts)
                    bool found = false;
                    for (int retry = 0; retry < 10; retry++)
                    {
                        budgetRoll = tierMinBudget + (float)rng.NextDouble() * (tierMaxBudget - tierMinBudget);
                        budgetPct = maxBudget > 0 ? (budgetRoll / maxBudget) * 100f : 0;

                        stats = hasStats
                            ? RollStats(rng, budgetRoll, presentTypes, eligibleList, analysis, ruleset)
                            : RollStatsForSpellItem(rng, budgetRoll, eligibleList, family);

                        candidate = new VariantData
                        {
                            name = name,
                            budgetPct = budgetPct,
                            tierLabel = tierLabel,
                            tierPosition = tierPosition,
                            stats = stats
                        };
                        fp = BuildVariantFingerprint(candidate);
                        if (!fingerprints.Contains(fp)) { found = true; break; }
                    }
                    if (!found) continue; // skip this slot if truly stuck
                }

                fingerprints.Add(fp);
                variants.Add(candidate);
            }
        }

        return variants.OrderBy(v => v.budgetPct).ToList();
    }

    /// <summary>Allocate variant slots across tiers with generous upper-tier representation.</summary>
    private int[] AllocateTierSlots(List<TierRange> tiers, int totalVariants)
    {
        var allocations = new int[tiers.Count];

        if (tiers.Count == 0) return allocations;
        if (tiers.Count == 1) { allocations[0] = totalVariants; return allocations; }

        // Upper tiers get a guaranteed minimum:
        // - Top tier (Gods): 1 guaranteed
        // - Other upper tiers (Power, Glory): 2 each
        // - Bottom tier (Variation): whatever remains
        // For 10 variants default: 5 Variation, 2 Power, 2 Glory, 1 Gods
        int upperSlots = 0;
        for (int i = 1; i < tiers.Count; i++)
        {
            bool isTopTier = (i == tiers.Count - 1);
            allocations[i] = isTopTier ? 1 : 2;
            upperSlots += allocations[i];
        }

        // Bottom tier (Variation) gets the rest, at least 1
        allocations[0] = Math.Max(1, totalVariants - upperSlots);

        // If we overallocated (very few variants requested), scale down
        int total = allocations.Sum();
        while (total > totalVariants)
        {
            // Remove from the tier with the most slots (but keep at least 1 each)
            int maxIdx = 0;
            for (int i = 1; i < allocations.Length; i++)
                if (allocations[i] > allocations[maxIdx]) maxIdx = i;
            if (allocations[maxIdx] > 1) { allocations[maxIdx]--; total--; }
            else break;
        }

        return allocations;
    }

    /// <summary>Estimate a stat budget from item_level for spell-effect-only items.</summary>
    private float EstimateBudgetFromItemLevel(int itemLevel)
    {
        // Rough approximation: vanilla items scale roughly linearly
        // A level 60 epic (ilvl ~66-83) typically has ~40-80 total weighted budget
        // A level 60 rare (ilvl ~52-63) typically has ~25-50
        // Simple linear: budget ≈ itemLevel * 0.7
        return Math.Max(5f, itemLevel * 0.7f);
    }

    /// <summary>Roll bonus stats for a spell-effect-only item.</summary>
    private List<StatRoll> RollStatsForSpellItem(Random rng, float budgetRoll, List<int> eligibleList, string family)
    {
        // Spell-effect items get 1-3 bonus stat slots (scaled down — the spell IS the main value)
        // Budget is reduced to 40% since the spell effect is the primary value
        float statBudget = budgetRoll * 0.40f;

        int slotCount = statBudget < 10 ? 1 : (statBudget < 25 ? 2 : 3);
        var chosenTypes = eligibleList.OrderBy(_ => rng.Next()).Take(slotCount).ToList();

        var weights = chosenTypes.Select(t => DEFAULT_STAT_WEIGHTS.GetValueOrDefault(t, 1.0f)).ToArray();
        float totalWeight = weights.Sum();
        var rolledStats = new List<StatRoll>();

        float remaining = statBudget;
        for (int s = 0; s < chosenTypes.Count; s++)
        {
            float share;
            if (s == chosenTypes.Count - 1)
                share = remaining;
            else
            {
                float basePortion = statBudget * (weights[s] / totalWeight);
                float jitter = (float)(rng.NextDouble() * 0.2 - 0.1) * basePortion;
                share = Math.Max(1, basePortion + jitter);
            }

            int statValue = Math.Max(1, (int)Math.Round(share / weights[s]));
            float actualCost = statValue * weights[s];
            remaining -= actualCost;

            rolledStats.Add(new StatRoll
            {
                statType = chosenTypes[s],
                statValue = statValue,
                name = STAT_NAMES.GetValueOrDefault(chosenTypes[s], $"Type{chosenTypes[s]}")
            });
        }

        return rolledStats;
    }

    /// <summary>Apply tier name as prefix or suffix.</summary>
    private string ApplyTierName(string baseName, string tierLabel, string tierPosition)
    {
        if (string.IsNullOrEmpty(tierLabel)) return baseName;

        if (tierPosition == "prefix")
            return tierLabel + " " + baseName;
        else
            return baseName + " " + tierLabel;
    }

    /// <summary>Converts VariantData list to anonymous objects for JSON serialization.</summary>
    private List<object> VariantsToJson(List<VariantData> variants)
    {
        return variants.Select((v, idx) => (object)new
        {
            variantIndex = idx,
            name = v.name,
            budgetPct = Math.Round(v.budgetPct, 1),
            tierLabel = v.tierLabel,
            tierPosition = v.tierPosition,
            stats = v.stats.Select(s => (object)new { s.statType, s.statValue, s.name }).ToList()
        }).ToList();
    }

    /// <summary>Converts VariantData to CommitRoll for DB writes.</summary>
    private CommitRoll VariantToCommitRoll(VariantData v)
    {
        return new CommitRoll
        {
            budgetPct = v.budgetPct,
            tierLabel = v.tierLabel ?? "",
            tierPosition = v.tierPosition ?? "suffix",
            stats = v.stats.Select(s => new CommitStat
            {
                statType = s.statType,
                statValue = s.statValue
            }).ToArray()
        };
    }

    private List<StatRoll> RollStats(Random rng, float budgetRoll, int[] presentTypes,
        List<int> eligibleList, dynamic analysis, RulesetDto ruleset)
    {
        int baseSlotCount = ((List<object>)analysis.stats).Count;
        int slotCount = baseSlotCount;
        if (ruleset.allowNewAffixes && rng.NextDouble() < 0.2 && baseSlotCount < 5)
            slotCount = Math.Min(baseSlotCount + ruleset.maxAffixCountChange, 10);

        var chosenTypes = new List<int>();
        var shuffledPresent = presentTypes.OrderBy(_ => rng.Next()).ToList();
        chosenTypes.AddRange(shuffledPresent.Take(slotCount));
        while (chosenTypes.Count < slotCount)
        {
            var pool = eligibleList.Where(s => !chosenTypes.Contains(s)).ToList();
            if (pool.Count == 0) break;
            chosenTypes.Add(pool[rng.Next(pool.Count)]);
        }

        var weights = chosenTypes.Select(t => DEFAULT_STAT_WEIGHTS.GetValueOrDefault(t, 1.0f)).ToArray();
        float totalWeight = weights.Sum();
        var rolledStats = new List<StatRoll>();

        float budgetRemaining = budgetRoll;
        for (int s = 0; s < chosenTypes.Count; s++)
        {
            float share;
            if (s == chosenTypes.Count - 1)
                share = budgetRemaining;
            else
            {
                float basePortion = budgetRoll * (weights[s] / totalWeight);
                float jitter = (float)(rng.NextDouble() * 0.3 - 0.15) * basePortion;
                share = Math.Max(1, basePortion + jitter);
            }

            int statValue = Math.Max(1, (int)Math.Round(share / weights[s]));
            float actualCost = statValue * weights[s];
            budgetRemaining -= actualCost;

            rolledStats.Add(new StatRoll
            {
                statType = chosenTypes[s],
                statValue = statValue,
                name = STAT_NAMES.GetValueOrDefault(chosenTypes[s], $"Type{chosenTypes[s]}")
            });
        }

        return rolledStats;
    }

    private string BuildFingerprint(dynamic analysis)
    {
        var stats = (List<object>)analysis.stats;
        var parts = stats.Select(s => $"{((dynamic)s).statType}:{((dynamic)s).statValue}").OrderBy(x => x);
        return string.Join("|", parts);
    }

    private string BuildVariantFingerprint(VariantData v)
    {
        var parts = v.stats.Select(s => $"{s.statType}:{s.statValue}").OrderBy(x => x);
        return string.Join("|", parts);
    }

    private List<TierRange> GetRequiredTiers(RulesetDto ruleset)
    {
        if (ruleset.namingTiers != null && ruleset.namingTiers.Length > 0)
        {
            return ruleset.namingTiers
                .Where(t => !string.IsNullOrEmpty(t.label))
                .Select(t => new TierRange
                {
                    minPct = t.minPct,
                    maxPct = t.maxPct,
                    label = t.label ?? "",
                    position = t.position ?? "suffix"
                })
                .ToList();
        }

        return new List<TierRange>
        {
            new() { minPct = 0, maxPct = 79, label = "Improved", position = "prefix" },
            new() { minPct = 80, maxPct = 89, label = "of Power", position = "suffix" },
            new() { minPct = 90, maxPct = 97, label = "of Glory", position = "suffix" },
            new() { minPct = 98, maxPct = 100, label = "of the Gods", position = "suffix" }
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  DB WRITE HELPERS
    // ══════════════════════════════════════════════════════════════

    /// <summary>Fast variant insert using pre-cached column list (no schema query per call).</summary>
    private async Task InsertVariantItemFast(MySqlConnector.MySqlConnection conn, List<string> columns,
        dynamic baseItem, int newEntry, CommitRoll roll)
    {
        int baseEntry = (int)baseItem.entry;
        int basePatch = GetPropInt(baseItem, "patch");

        var statTypes = new int[10];
        var statValues = new int[10];
        for (int i = 0; i < 10; i++)
        {
            statTypes[i] = GetPropInt(baseItem, $"stat_type{i + 1}");
            statValues[i] = GetPropInt(baseItem, $"stat_value{i + 1}");
        }
        for (int i = 0; i < Math.Min(roll.stats.Length, 10); i++)
        {
            statTypes[i] = roll.stats[i].statType;
            statValues[i] = roll.stats[i].statValue;
        }
        for (int i = roll.stats.Length; i < 10; i++)
        {
            statTypes[i] = 0;
            statValues[i] = 0;
        }

        string tierLabel = roll.tierLabel ?? "";
        string tierPosition = roll.tierPosition ?? "suffix";

        var selectParts = new List<string>();
        foreach (var col in columns)
        {
            if (col == "entry")
                selectParts.Add($"{newEntry} AS `entry`");
            else if (col == "name")
            {
                if (tierPosition == "prefix" && !string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(@TierLabel, ' ', name) AS `name`");
                else if (!string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(name, ' ', @TierLabel) AS `name`");
                else
                    selectParts.Add("`name`");
            }
            else if (col.StartsWith("stat_type") && col.Length <= 11)
            {
                int idx = int.Parse(col.Replace("stat_type", "")) - 1;
                selectParts.Add($"@ST{idx} AS `{col}`");
            }
            else if (col.StartsWith("stat_value") && col.Length <= 12)
            {
                int idx = int.Parse(col.Replace("stat_value", "")) - 1;
                selectParts.Add($"@SV{idx} AS `{col}`");
            }
            else
                selectParts.Add($"`{col}`");
        }

        var sql = $"INSERT IGNORE INTO item_template SELECT {string.Join(", ", selectParts)} FROM item_template WHERE entry = @BaseEntry AND patch = @BasePatch";

        await conn.ExecuteAsync(sql, new
        {
            BaseEntry = baseEntry,
            BasePatch = basePatch,
            TierLabel = tierLabel,
            ST0 = statTypes[0],
            SV0 = statValues[0],
            ST1 = statTypes[1],
            SV1 = statValues[1],
            ST2 = statTypes[2],
            SV2 = statValues[2],
            ST3 = statTypes[3],
            SV3 = statValues[3],
            ST4 = statTypes[4],
            SV4 = statValues[4],
            ST5 = statTypes[5],
            SV5 = statValues[5],
            ST6 = statTypes[6],
            SV6 = statValues[6],
            ST7 = statTypes[7],
            SV7 = statValues[7],
            ST8 = statTypes[8],
            SV8 = statValues[8],
            ST9 = statTypes[9],
            SV9 = statValues[9]
        });

        // Only apply gold multiplier if it actually changes the price meaningfully
        float goldMult = GetGoldMultiplier(roll.budgetPct);
        if (goldMult > 1.05f)
        {
            await conn.ExecuteAsync(
                "UPDATE item_template SET buy_price = ROUND(buy_price * @Mult), sell_price = ROUND(sell_price * @Mult) WHERE entry = @Entry",
                new { Mult = goldMult, Entry = newEntry });
        }

        // Promote Glory (90%+) and Gods (98%+) variants to Epic quality
        if (roll.budgetPct >= 90)
        {
            await conn.ExecuteAsync(
                "UPDATE item_template SET quality = 4 WHERE entry = @Entry AND quality < 4",
                new { Entry = newEntry });
        }
    }
    private async Task<int> ExpandLootTableFast(MySqlConnector.MySqlConnection mangosConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)> trackingRows,
        int lootId, int baseItemEntry, List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        int rowsAdded = 0;

        var directRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount, patch_min, patch_max
            FROM creature_loot_template
            WHERE entry = @LootId AND item = @Item AND mincountOrRef > 0",
            new { LootId = lootId, Item = baseItemEntry });

        if (directRow != null)
            return await ExpandIntoGroupFast(mangosConn, trackingRows, "creature_loot_template",
                directRow, variantEntries, rolls, creatureEntry);

        var refPtrs = await mangosConn.QueryAsync<dynamic>(@"
            SELECT entry, item, mincountOrRef FROM creature_loot_template
            WHERE entry = @LootId AND mincountOrRef < 0",
            new { LootId = lootId });

        foreach (var ptr in refPtrs)
        {
            int refEntry = Math.Abs((int)ptr.mincountOrRef);
            var refRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount, patch_min, patch_max
                FROM reference_loot_template
                WHERE entry = @RefEntry AND item = @Item",
                new { RefEntry = refEntry, Item = baseItemEntry });

            if (refRow != null)
            {
                rowsAdded = await ExpandIntoGroupFast(mangosConn, trackingRows, "reference_loot_template",
                    refRow, variantEntries, rolls, creatureEntry);
                break;
            }
        }

        return rowsAdded;
    }

    private async Task<int> ExpandIntoGroupFast(MySqlConnector.MySqlConnection mangosConn,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)> trackingRows,
        string tableName, dynamic baseRow, List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        int rowsAdded = 0;
        int groupId = (int)baseRow.groupid;
        int lootEntry = (int)baseRow.entry;
        float originalChance = (float)baseRow.chance;

        var groupItems = (await mangosConn.QueryAsync<dynamic>(
            $"SELECT item, ChanceOrQuestChance AS chance FROM `{tableName}` WHERE entry = @Entry AND groupid = @GroupId",
            new { Entry = lootEntry, GroupId = groupId })).ToList();

        float baseShare;
        if (originalChance == 0)
        {
            float explicitTotal = groupItems.Where(i => (float)i.chance > 0).Sum(i => (float)i.chance);
            int equalItems = groupItems.Count(i => (float)i.chance == 0);
            baseShare = equalItems > 0 ? (100f - explicitTotal) / equalItems : 0;
        }
        else
        {
            baseShare = Math.Abs(originalChance);
        }

        float originalNewShare = baseShare * 0.40f;
        float variantBudget = baseShare - originalNewShare;

        float[] variantWeights = new float[rolls.Length];
        for (int i = 0; i < rolls.Length; i++)
            variantWeights[i] = Math.Max(0.1f, 100f - rolls[i].budgetPct);
        float weightSum = variantWeights.Sum();

        // Track the modification (collected, not inserted yet)
        trackingRows.Add((creatureEntry, tableName, lootEntry, (int)baseRow.item, "modified", originalChance, originalNewShare));

        await mangosConn.ExecuteAsync(
            $"UPDATE `{tableName}` SET ChanceOrQuestChance = @Chance WHERE entry = @Entry AND item = @Item AND groupid = @GroupId",
            new { Chance = originalNewShare, Entry = lootEntry, Item = (int)baseRow.item, GroupId = groupId });

        for (int i = 0; i < variantEntries.Count && i < rolls.Length; i++)
        {
            float variantChance = (float)Math.Round(variantBudget * (variantWeights[i] / weightSum), 4);

            await mangosConn.ExecuteAsync(
                $@"INSERT IGNORE INTO `{tableName}` (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
                   VALUES (@Entry, @Item, @Chance, @GroupId, 1, 1, @PMin, @PMax)",
                new
                {
                    Entry = lootEntry,
                    Item = variantEntries[i],
                    Chance = variantChance,
                    GroupId = groupId,
                    PMin = (int)baseRow.patch_min,
                    PMax = (int)baseRow.patch_max
                });

            trackingRows.Add((creatureEntry, tableName, lootEntry, variantEntries[i], "inserted", 0, variantChance));
            rowsAdded++;
        }

        return rowsAdded;
    }

    // The original InsertVariantItem (used by single-source commit, unchanged)

    private async Task InsertVariantItem(MySqlConnector.MySqlConnection conn, dynamic baseItem, int newEntry, CommitRoll roll)
    {
        int baseEntry = (int)baseItem.entry;
        int basePatch = GetPropInt(baseItem, "patch");

        int baseStatCount = 0;
        for (int i = 1; i <= 10; i++)
        {
            if (GetPropInt(baseItem, $"stat_type{i}") > 0) baseStatCount = i;
        }

        var statTypes = new int[10];
        var statValues = new int[10];

        // Copy existing base stats for slots that won't be overwritten
        for (int i = 0; i < 10; i++)
        {
            statTypes[i] = GetPropInt(baseItem, $"stat_type{i + 1}");
            statValues[i] = GetPropInt(baseItem, $"stat_value{i + 1}");
        }

        // Overwrite with rolled stats
        for (int i = 0; i < Math.Min(roll.stats.Length, 10); i++)
        {
            statTypes[i] = roll.stats[i].statType;
            statValues[i] = roll.stats[i].statValue;
        }
        // Clear any remaining slots beyond the rolled count
        for (int i = roll.stats.Length; i < 10; i++)
        {
            statTypes[i] = 0;
            statValues[i] = 0;
        }

        var columns = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'mangos' AND TABLE_NAME = 'item_template' ORDER BY ORDINAL_POSITION"
        )).ToList();

        // Build name with prefix or suffix
        string tierLabel = roll.tierLabel ?? "";
        string tierPosition = roll.tierPosition ?? "suffix";

        var selectParts = new List<string>();

        foreach (var col in columns)
        {
            if (col == "entry")
                selectParts.Add($"{newEntry} AS `entry`");
            else if (col == "name")
            {
                if (tierPosition == "prefix" && !string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(@TierLabel, ' ', name) AS `name`");
                else if (!string.IsNullOrEmpty(tierLabel))
                    selectParts.Add("CONCAT(name, ' ', @TierLabel) AS `name`");
                else
                    selectParts.Add("`name`");
            }
            else if (col.StartsWith("stat_type") && col.Length <= 11)
            {
                int idx = int.Parse(col.Replace("stat_type", "")) - 1;
                selectParts.Add($"@ST{idx} AS `{col}`");
            }
            else if (col.StartsWith("stat_value") && col.Length <= 12)
            {
                int idx = int.Parse(col.Replace("stat_value", "")) - 1;
                selectParts.Add($"@SV{idx} AS `{col}`");
            }
            else
                selectParts.Add($"`{col}`");
        }

        var sql = $"INSERT IGNORE INTO item_template SELECT {string.Join(", ", selectParts)} FROM item_template WHERE entry = @BaseEntry AND patch = @BasePatch";

        await conn.ExecuteAsync(sql, new
        {
            BaseEntry = baseEntry,
            BasePatch = basePatch,
            TierLabel = tierLabel,
            ST0 = statTypes[0],
            SV0 = statValues[0],
            ST1 = statTypes[1],
            SV1 = statValues[1],
            ST2 = statTypes[2],
            SV2 = statValues[2],
            ST3 = statTypes[3],
            SV3 = statValues[3],
            ST4 = statTypes[4],
            SV4 = statValues[4],
            ST5 = statTypes[5],
            SV5 = statValues[5],
            ST6 = statTypes[6],
            SV6 = statValues[6],
            ST7 = statTypes[7],
            SV7 = statValues[7],
            ST8 = statTypes[8],
            SV8 = statValues[8],
            ST9 = statTypes[9],
            SV9 = statValues[9]
        });

        // Apply gold multiplier
        float goldMult = GetGoldMultiplier(roll.budgetPct);
        if (goldMult > 1.0f)
        {
            await conn.ExecuteAsync(
                "UPDATE item_template SET buy_price = ROUND(buy_price * @Mult), sell_price = ROUND(sell_price * @Mult) WHERE entry = @Entry",
                new { Mult = goldMult, Entry = newEntry });
        }

        // Promote Glory (90%+) and Gods (98%+) variants to Epic quality
        if (roll.budgetPct >= 90)
        {
            await conn.ExecuteAsync(
                "UPDATE item_template SET quality = 4 WHERE entry = @Entry AND quality < 4",
                new { Entry = newEntry });
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  LEGENDARY GENERATION
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Generate a legendary preview (no DB writes) for display in the variant preview UI.
    /// Returns null if legendary can't be generated for these inputs.
    /// </summary>
    private async Task<object?> PreviewLegendary(
        MySqlConnector.MySqlConnection conn,
        int creatureEntry,
        List<int> eligibleItemEntries,
        RulesetDto ruleset)
    {
        if (eligibleItemEntries.Count == 0 || creatureEntry <= 0) return null;

        var rng = new Random();

        // Pick the item: user-chosen (legendaryItemEntry > 0) or random
        int chosenEntry = ruleset.legendaryItemEntry > 0 && eligibleItemEntries.Contains(ruleset.legendaryItemEntry)
            ? ruleset.legendaryItemEntry
            : eligibleItemEntries[rng.Next(eligibleItemEntries.Count)];

        var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM item_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
            new { E = chosenEntry });
        if (item == null) return null;

        var creature = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT name FROM creature_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
            new { E = creatureEntry });
        string bossName = creature != null ? (string)creature.name : $"Boss #{creatureEntry}";

        var analysis = AnalyzeItemStats(item);
        bool hasStats = (int)analysis.totalStats > 0;
        bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
        if (!hasStats && !hasSpellEffects) return null;

        float baseBudget = (float)analysis.weightedBudget;
        if (!hasStats && hasSpellEffects)
            baseBudget = EstimateBudgetFromItemLevel(GetPropInt(item, "item_level"));

        float legendaryBudget = baseBudget * 1.50f;
        int[] presentTypes = (int[])analysis.presentStatTypes;
        string family = (string)analysis.detectedFamily;

        var eligible = new HashSet<int>(presentTypes);
        var familyStats = STAT_FAMILIES.GetValueOrDefault(family, STAT_FAMILIES["hybrid"]);
        foreach (var s in familyStats) eligible.Add(s);
        var eligibleList = eligible.ToList();

        List<StatRoll> stats;
        if (hasStats)
            stats = RollStats(rng, legendaryBudget, presentTypes, eligibleList, analysis, ruleset);
        else
            stats = RollStatsForSpellItem(rng, legendaryBudget, eligibleList, family);

        string itemName = (string)item.name;
        string legendaryName = BuildLegendaryName(bossName, itemName, family, ruleset);

        uint displayId = (uint)item.display_id;
        string iconPath = _dbc.GetItemIconPath(displayId);

        return new
        {
            baseItemEntry = chosenEntry,
            baseItemName = itemName,
            baseItemQuality = (int)item.quality,
            displayId,
            iconPath,
            legendaryName,
            bossName,
            budgetPct = 150.0,
            dropPct = ruleset.legendaryDropPct,
            quality = 5,
            stats = stats.Select(s => (object)new { s.statType, s.statValue, s.name }).ToList()
        };
    }

    /// <summary>
    /// Generate and insert a single legendary variant for a creature.
    /// Picks one item (user-chosen or random), creates a 150% budget legendary with boss name.
    /// Inserts directly into creature_loot_template at the configured effective drop %.
    /// </summary>
    private async Task<int> GenerateAndInsertLegendary(
        MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn,
        List<string> columns,
        int creatureEntry, int lootId,
        List<int> eligibleItemEntries, RulesetDto ruleset,
        List<(int genEntry, int baseEntry, int creatureEntry, float budgetPct, string tierName)>? trackingItemRows = null,
        List<(int creatureEntry, string table, int lootEntry, int itemEntry, string action, float origChance, float newChance)>? trackingLootRows = null)
    {
        if (eligibleItemEntries.Count == 0) return 0;

        var rng = new Random();

        // Pick the item: user-chosen (legendaryItemEntry > 0) or random
        int chosenEntry = ruleset.legendaryItemEntry > 0 && eligibleItemEntries.Contains(ruleset.legendaryItemEntry)
            ? ruleset.legendaryItemEntry
            : eligibleItemEntries[rng.Next(eligibleItemEntries.Count)];

        // Load base item
        var item = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM item_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
            new { E = chosenEntry });
        if (item == null) return 0;

        // Load creature name for boss naming
        var creature = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT name FROM creature_template
            WHERE entry = @E AND patch = (SELECT MAX(patch) FROM creature_template ct2 WHERE ct2.entry = @E)",
            new { E = creatureEntry });
        string bossName = creature != null ? (string)creature.name : $"Boss #{creatureEntry}";

        // Analyze the item
        var analysis = AnalyzeItemStats(item);
        bool hasStats = (int)analysis.totalStats > 0;
        bool hasSpellEffects = ((List<SpellEffectInfo>)analysis.spellEffects).Count > 0;
        if (!hasStats && !hasSpellEffects) return 0;

        // Roll stats at 150% of base budget
        float baseBudget = (float)analysis.weightedBudget;
        if (!hasStats && hasSpellEffects)
            baseBudget = EstimateBudgetFromItemLevel(GetPropInt(item, "item_level"));

        float legendaryBudget = baseBudget * 1.50f;
        int[] presentTypes = (int[])analysis.presentStatTypes;
        string family = (string)analysis.detectedFamily;

        var eligible = new HashSet<int>(presentTypes);
        var familyStats = STAT_FAMILIES.GetValueOrDefault(family, STAT_FAMILIES["hybrid"]);
        foreach (var s in familyStats) eligible.Add(s);
        var eligibleList = eligible.ToList();

        List<StatRoll> stats;
        if (hasStats)
            stats = RollStats(rng, legendaryBudget, presentTypes, eligibleList, analysis, ruleset);
        else
            stats = RollStatsForSpellItem(rng, legendaryBudget, eligibleList, family);

        // Build legendary name
        string itemName = (string)item.name;
        string legendaryName = BuildLegendaryName(bossName, itemName, family, ruleset);

        // Create the commit roll
        var roll = new CommitRoll
        {
            budgetPct = 150f, // over 100% — this is legendary territory
            tierLabel = legendaryName.Contains(itemName)
                ? legendaryName.Replace(itemName, "").Trim()
                : legendaryName,
            tierPosition = "full", // special: name is fully replaced
            stats = stats.Select(s => new CommitStat { statType = s.statType, statValue = s.statValue }).ToArray()
        };

        // Get next ID and insert
        int newEntry = await GetNextLootifierId(adminConn);

        if (columns != null)
            await InsertVariantItemFast(mangosConn, columns, item, newEntry, roll);
        else
            await InsertVariantItem(mangosConn, item, newEntry, roll);

        // Override the name completely (InsertVariantItem builds prefix/suffix, but legendary needs full name)
        await mangosConn.ExecuteAsync(
            "UPDATE item_template SET name = @Name, quality = 5 WHERE entry = @Entry",
            new { Name = legendaryName, Entry = newEntry });

        // Gold multiplier for legendary: 3x
        await mangosConn.ExecuteAsync(
            "UPDATE item_template SET buy_price = ROUND(buy_price * 3), sell_price = ROUND(sell_price * 3) WHERE entry = @Entry",
            new { Entry = newEntry });

        // Track the generated item
        if (trackingItemRows != null)
        {
            trackingItemRows.Add((newEntry, chosenEntry, creatureEntry, 150f, "Legendary"));
        }
        else
        {
            await adminConn.ExecuteAsync(@"
                INSERT INTO lootifier_generated_items
                    (generated_entry, base_entry, creature_entry, budget_pct, tier_name, created_at)
                VALUES (@GenEntry, @BaseEntry, @CreatureEntry, 150, 'Legendary', NOW())",
                new { GenEntry = newEntry, BaseEntry = chosenEntry, CreatureEntry = creatureEntry });
        }

        // Insert into loot table as a direct drop at the configured effective %
        // Use a high group ID to avoid conflicts with existing groups
        int legendaryGroupId = 99;
        float dropChance = ruleset.legendaryDropPct;

        await mangosConn.ExecuteAsync(@"
            INSERT IGNORE INTO creature_loot_template (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
            VALUES (@Entry, @Item, @Chance, @GroupId, 1, 1, 0, 10)",
            new { Entry = lootId, Item = newEntry, Chance = dropChance, GroupId = legendaryGroupId });

        // Track the loot insertion
        if (trackingLootRows != null)
        {
            trackingLootRows.Add((creatureEntry, "creature_loot_template", lootId, newEntry, "inserted", 0, dropChance));
        }
        else
        {
            await adminConn.ExecuteAsync(@"
                INSERT INTO lootifier_loot_entries
                    (creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance, created_at)
                VALUES (@CE, 'creature_loot_template', @Entry, @Item, 'inserted', 0, @Chance, NOW())",
                new { CE = creatureEntry, Entry = lootId, Item = newEntry, Chance = dropChance });
        }

        return 1;
    }

    /// <summary>Build the legendary item name from boss name + item name.</summary>
    private string BuildLegendaryName(string bossName, string itemName, string family, RulesetDto ruleset)
    {
        // Check if boss name overlaps with item name (any word ≥ 4 chars in common)
        var bossWords = bossName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'', '\u2018', '\u2019', ',', '.').ToLowerInvariant())
            .Where(w => w.Length >= 4)
            .ToHashSet();

        var itemWords = itemName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'', '\u2018', '\u2019', ',', '.').ToLowerInvariant())
            .Where(w => w.Length >= 4)
            .ToHashSet();

        bool hasOverlap = bossWords.Overlaps(itemWords);

        if (hasOverlap)
        {
            // Item already references the boss (e.g., "Smite's Mighty Reaper")
            // Use family-appropriate suffix
            string suffix = family switch
            {
                "physical" => ruleset.legendarySuffixMelee,
                "caster" => ruleset.legendarySuffixCaster,
                _ => ruleset.legendarySuffixMelee // hybrid defaults to melee
            };
            // Check item class for ranged weapons
            // (We don't have item class here easily, so hybrid check is enough)
            return itemName + " " + suffix;
        }
        else
        {
            // No overlap — prefix with possessive boss name
            // "Edwin VanCleef" → "Edwin VanCleef's"
            string possessive = bossName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? bossName + "'"
                : bossName + "'s";
            return possessive + " " + itemName;
        }
    }

    private float GetGoldMultiplier(float budgetPct)
    {
        if (budgetPct >= 98) return 2.0f;
        if (budgetPct >= 90) return 1.8f;
        if (budgetPct >= 80) return 1.6f;
        float t = Math.Clamp(budgetPct / 79f, 0f, 1f);
        return 1.01f + t * 0.58f;
    }

    private async Task<int> ExpandLootTable(MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn, int lootId, int baseItemEntry,
        List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        int rowsAdded = 0;

        var directRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount, patch_min, patch_max
            FROM creature_loot_template
            WHERE entry = @LootId AND item = @Item AND mincountOrRef > 0",
            new { LootId = lootId, Item = baseItemEntry });

        if (directRow != null)
            return await ExpandIntoGroup(mangosConn, adminConn, "creature_loot_template",
                directRow, variantEntries, rolls, creatureEntry);

        var refPtrs = await mangosConn.QueryAsync<dynamic>(@"
            SELECT entry, item, mincountOrRef FROM creature_loot_template
            WHERE entry = @LootId AND mincountOrRef < 0",
            new { LootId = lootId });

        foreach (var ptr in refPtrs)
        {
            int refEntry = Math.Abs((int)ptr.mincountOrRef);
            var refRow = await mangosConn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT entry, item, ChanceOrQuestChance AS chance, groupid, mincountOrRef, maxcount, patch_min, patch_max
                FROM reference_loot_template
                WHERE entry = @RefEntry AND item = @Item",
                new { RefEntry = refEntry, Item = baseItemEntry });

            if (refRow != null)
            {
                rowsAdded = await ExpandIntoGroup(mangosConn, adminConn, "reference_loot_template",
                    refRow, variantEntries, rolls, creatureEntry);
                break;
            }
        }

        return rowsAdded;
    }

    private async Task<int> ExpandIntoGroup(MySqlConnector.MySqlConnection mangosConn,
        MySqlConnector.MySqlConnection adminConn, string tableName,
        dynamic baseRow, List<int> variantEntries, CommitRoll[] rolls, int creatureEntry)
    {
        int rowsAdded = 0;
        int groupId = (int)baseRow.groupid;
        int lootEntry = (int)baseRow.entry;
        float originalChance = (float)baseRow.chance;

        var groupItems = (await mangosConn.QueryAsync<dynamic>(
            $"SELECT item, ChanceOrQuestChance AS chance FROM `{tableName}` WHERE entry = @Entry AND groupid = @GroupId",
            new { Entry = lootEntry, GroupId = groupId })).ToList();

        float baseShare;
        if (originalChance == 0)
        {
            float explicitTotal = groupItems.Where(i => (float)i.chance > 0).Sum(i => (float)i.chance);
            int equalItems = groupItems.Count(i => (float)i.chance == 0);
            baseShare = equalItems > 0 ? (100f - explicitTotal) / equalItems : 0;
        }
        else
        {
            baseShare = Math.Abs(originalChance);
        }

        float originalNewShare = baseShare * 0.40f;
        float variantBudget = baseShare - originalNewShare;

        float[] variantWeights = new float[rolls.Length];
        for (int i = 0; i < rolls.Length; i++)
            variantWeights[i] = Math.Max(0.1f, 100f - rolls[i].budgetPct);
        float weightSum = variantWeights.Sum();

        await adminConn.ExecuteAsync(@"
            INSERT INTO lootifier_loot_entries
                (creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance, created_at)
            VALUES (@CE, @Table, @Entry, @Item, 'modified', @OrigChance, @NewChance, NOW())",
            new
            {
                CE = creatureEntry,
                Table = tableName,
                Entry = lootEntry,
                Item = (int)baseRow.item,
                OrigChance = originalChance,
                NewChance = originalNewShare
            });

        await mangosConn.ExecuteAsync(
            $"UPDATE `{tableName}` SET ChanceOrQuestChance = @Chance WHERE entry = @Entry AND item = @Item AND groupid = @GroupId",
            new { Chance = originalNewShare, Entry = lootEntry, Item = (int)baseRow.item, GroupId = groupId });

        for (int i = 0; i < variantEntries.Count && i < rolls.Length; i++)
        {
            float variantChance = (float)Math.Round(variantBudget * (variantWeights[i] / weightSum), 4);

            await mangosConn.ExecuteAsync(
                $@"INSERT IGNORE INTO `{tableName}` (entry, item, ChanceOrQuestChance, groupid, mincountOrRef, maxcount, patch_min, patch_max)
                   VALUES (@Entry, @Item, @Chance, @GroupId, 1, 1, @PMin, @PMax)",
                new
                {
                    Entry = lootEntry,
                    Item = variantEntries[i],
                    Chance = variantChance,
                    GroupId = groupId,
                    PMin = (int)baseRow.patch_min,
                    PMax = (int)baseRow.patch_max
                });

            await adminConn.ExecuteAsync(@"
                INSERT INTO lootifier_loot_entries
                    (creature_entry, loot_table, loot_entry, item_entry, action_type, original_chance, new_chance, created_at)
                VALUES (@CE, @Table, @Entry, @Item, 'inserted', 0, @NewChance, NOW())",
                new { CE = creatureEntry, Table = tableName, Entry = lootEntry, Item = variantEntries[i], NewChance = variantChance });

            rowsAdded++;
        }

        return rowsAdded;
    }

    // ══════════════════════════════════════════════════════════════
    //  ANALYSIS
    // ══════════════════════════════════════════════════════════════

    private dynamic AnalyzeItemStats(dynamic item)
    {
        var stats = new List<object>();
        int totalStats = 0;
        float weightedBudget = 0;
        var presentTypes = new HashSet<int>();

        for (int i = 1; i <= 10; i++)
        {
            int statType = GetPropInt(item, $"stat_type{i}");
            int statValue = GetPropInt(item, $"stat_value{i}");
            if (statType > 0 && statValue != 0)
            {
                string name = STAT_NAMES.GetValueOrDefault(statType, $"Type{statType}");
                float weight = DEFAULT_STAT_WEIGHTS.GetValueOrDefault(statType, 1.0f);
                stats.Add(new { slot = i, statType, statValue, name, weight, weightedCost = statValue * weight });
                totalStats += Math.Abs(statValue);
                weightedBudget += Math.Abs(statValue) * weight;
                presentTypes.Add(statType);
            }
        }

        string family = "hybrid";
        if (presentTypes.IsSubsetOf(STAT_FAMILIES["physical"])) family = "physical";
        else if (presentTypes.IsSubsetOf(STAT_FAMILIES["caster"])) family = "caster";

        // Analyze spell effects
        var spellEffects = new List<SpellEffectInfo>();
        for (int i = 1; i <= 5; i++)
        {
            int spellId = GetPropInt(item, $"spellid_{i}");
            int spellTrigger = GetPropInt(item, $"spelltrigger_{i}");
            if (spellId > 0)
            {
                string triggerName = spellTrigger switch
                {
                    SPELLTRIGGER_USE => "Use",
                    SPELLTRIGGER_EQUIP => "Equip",
                    SPELLTRIGGER_CHANCE_ON_HIT => "Chance on Hit",
                    _ => $"Trigger {spellTrigger}"
                };
                spellEffects.Add(new SpellEffectInfo
                {
                    slot = i,
                    spellId = spellId,
                    triggerType = spellTrigger,
                    triggerName = triggerName
                });
            }
        }

        return new
        {
            stats,
            totalStats,
            weightedBudget,
            detectedFamily = family,
            presentStatTypes = presentTypes.ToArray(),
            spellEffects,
            hasSpellEffects = spellEffects.Count > 0
        };
    }

    private async Task<List<dynamic>> EnrichLootRows(MySqlConnector.MySqlConnection conn, List<LootifierLootRow> rows)
    {
        var result = new List<dynamic>();
        foreach (var row in rows)
        {
            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT entry, name, quality, class, subclass, inventory_type, display_id,
                       stat_type1, stat_value1, stat_type2, stat_value2, stat_type3, stat_value3,
                       stat_type4, stat_value4, stat_type5, stat_value5,
                       stat_type6, stat_value6, stat_type7, stat_value7, stat_type8, stat_value8,
                       stat_type9, stat_value9, stat_type10, stat_value10,
                       spellid_1, spelltrigger_1, spellid_2, spelltrigger_2,
                       spellid_3, spelltrigger_3, spellid_4, spelltrigger_4,
                       spellid_5, spelltrigger_5,
                       required_level, item_level
                FROM item_template WHERE entry = @E
                    AND patch = (SELECT MAX(patch) FROM item_template it2 WHERE it2.entry = @E)",
                new { E = row.item });

            var analysis = item != null ? AnalyzeItemStats(item) : null;

            result.Add(new
            {
                lootEntry = row.lootEntry,
                itemEntry = row.item,
                chance = row.chance,
                groupId = row.groupId,
                mincountOrRef = row.mincountOrRef,
                maxcount = row.maxcount,
                patchMin = row.patchMin,
                patchMax = row.patchMax,
                itemName = item != null ? (string)item.name : $"Item #{row.item}",
                quality = item != null ? (int)item.quality : 0,
                itemClass = item != null ? (int)item.@class : 0,
                displayId = item != null ? (uint)item.display_id : 0u,
                requiredLevel = item != null ? (int)item.required_level : 0,
                itemLevel = item != null ? (int)item.item_level : 0,
                totalStats = analysis?.totalStats ?? 0,
                weightedBudget = analysis?.weightedBudget ?? 0f,
                detectedFamily = analysis?.detectedFamily ?? "unknown",
                stats = analysis?.stats ?? new List<object>(),
                hasSpellEffects = analysis?.hasSpellEffects ?? false,
                spellEffects = analysis?.spellEffects ?? new List<SpellEffectInfo>()
            });
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════════
    //  INFRASTRUCTURE
    // ══════════════════════════════════════════════════════════════

    private async Task EnsureTrackingTables(MySqlConnector.MySqlConnection adminConn)
    {
        await adminConn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS lootifier_generated_items (
                id INT AUTO_INCREMENT PRIMARY KEY,
                generated_entry INT NOT NULL,
                base_entry INT NOT NULL,
                creature_entry INT NOT NULL,
                budget_pct FLOAT DEFAULT 0,
                tier_name VARCHAR(64) DEFAULT '',
                created_at DATETIME NOT NULL,
                INDEX idx_creature (creature_entry),
                INDEX idx_generated (generated_entry)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

        await adminConn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS lootifier_loot_entries (
                id INT AUTO_INCREMENT PRIMARY KEY,
                creature_entry INT NOT NULL,
                loot_table VARCHAR(64) NOT NULL,
                loot_entry INT NOT NULL,
                item_entry INT NOT NULL,
                action_type VARCHAR(16) NOT NULL,
                original_chance FLOAT DEFAULT 0,
                new_chance FLOAT DEFAULT 0,
                created_at DATETIME NOT NULL,
                INDEX idx_creature (creature_entry)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    }

    private async Task<int> GetNextLootifierId(MySqlConnector.MySqlConnection adminConn)
    {
        int fromTracking = LOOTIFIER_ID_START;
        if (await TableExists(adminConn, "lootifier_generated_items"))
        {
            var maxTracked = await adminConn.ExecuteScalarAsync<int?>("SELECT MAX(generated_entry) FROM lootifier_generated_items");
            if (maxTracked.HasValue)
                fromTracking = maxTracked.Value + 1;
        }

        // Also check item_template directly in case orphaned entries exist from failed commits
        using var mangosConn = _db.Mangos();
        var maxInItems = await mangosConn.ExecuteScalarAsync<int?>(
            "SELECT MAX(entry) FROM item_template WHERE entry >= @Start",
            new { Start = LOOTIFIER_ID_START });
        int fromItems = maxInItems.HasValue ? maxInItems.Value + 1 : LOOTIFIER_ID_START;

        return Math.Max(fromTracking, fromItems);
    }

    private async Task<bool> TableExists(MySqlConnector.MySqlConnection conn, string tableName)
    {
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @T",
            new { T = tableName }) > 0;
    }

    private int GetPropInt(dynamic obj, string name)
    {
        var dict = obj as IDictionary<string, object>;
        if (dict != null && dict.TryGetValue(name, out var val))
            return val == null ? 0 : Convert.ToInt32(val);
        return 0;
    }
}

// ══════════════════════════════════════════════════════════════
//  INTERNAL TYPES
// ══════════════════════════════════════════════════════════════

internal class SpellEffectInfo
{
    public int slot { get; set; }
    public int spellId { get; set; }
    public int triggerType { get; set; }
    public string triggerName { get; set; } = "";
}

internal class VariantData
{
    public string name { get; set; } = "";
    public float budgetPct { get; set; }
    public string tierLabel { get; set; } = "";
    public string tierPosition { get; set; } = "suffix";
    public List<StatRoll> stats { get; set; } = new();
}

internal class StatRoll
{
    public int statType { get; set; }
    public int statValue { get; set; }
    public string name { get; set; } = "";
}

internal class TierRange
{
    public float minPct { get; set; }
    public float maxPct { get; set; }
    public string label { get; set; } = "";
    public string position { get; set; } = "suffix";
}

// ══════════════════════════════════════════════════════════════
//  DTOs
// ══════════════════════════════════════════════════════════════

public class LootifierLootRow
{
    public int lootEntry { get; set; }
    public int item { get; set; }
    public float chance { get; set; }
    public int groupId { get; set; }
    public int mincountOrRef { get; set; }
    public int maxcount { get; set; }
    public int patchMin { get; set; }
    public int patchMax { get; set; }
}

public class RulesetDto
{
    public float budgetCeilingPct { get; set; } = 35;
    public int variantsPerItem { get; set; } = 10;
    public bool allowNewAffixes { get; set; } = true;
    public int maxAffixCountChange { get; set; } = 1;
    public string dropChanceStrategy { get; set; } = "preserve";
    public NamingTierDto[]? namingTiers { get; set; }
    // Legendary system
    public bool generateLegendary { get; set; } = false;
    public float legendaryDropPct { get; set; } = 0.2f;
    public string legendarySuffixMelee { get; set; } = "of Destruction";
    public string legendarySuffixRanged { get; set; } = "of the Hunt";
    public string legendarySuffixCaster { get; set; } = "of Arcana";
    public int legendaryItemEntry { get; set; } = 0; // Single mode: user-chosen item. 0 = random.
}

public class NamingTierDto
{
    public float minPct { get; set; }
    public float maxPct { get; set; }
    public string? label { get; set; }
    public string? position { get; set; }
}

public class GenerateRequest
{
    public int creatureEntry { get; set; }
    public int[] itemEntries { get; set; } = Array.Empty<int>();
    public RulesetDto? ruleset { get; set; }
}

public class BatchRequest
{
    public int[]? qualities { get; set; }
    public int levelMin { get; set; }
    public int levelMax { get; set; }
    public int[]? creatureRanks { get; set; }
    public int[]? mapIds { get; set; }
    public RulesetDto? ruleset { get; set; }
}

public class BatchCommitRequest
{
    public BatchCreatureGroup[] creatures { get; set; } = Array.Empty<BatchCreatureGroup>();
    public RulesetDto? ruleset { get; set; }
}

public class BatchSampleRequest
{
    public int creatureEntry { get; set; }
    public int[] itemEntries { get; set; } = Array.Empty<int>();
    public RulesetDto? ruleset { get; set; }
}

public class BatchCreatureGroup
{
    public int creatureEntry { get; set; }
    public int[] itemEntries { get; set; } = Array.Empty<int>();
}

public class CommitRequest
{
    public int creatureEntry { get; set; }
    public CommitItemGroup[] variants { get; set; } = Array.Empty<CommitItemGroup>();
    public RulesetDto? ruleset { get; set; }
}

public class CommitItemGroup
{
    public int baseItemEntry { get; set; }
    public CommitRoll[] rolls { get; set; } = Array.Empty<CommitRoll>();
}

public class CommitRoll
{
    public float budgetPct { get; set; }
    public string? tierLabel { get; set; }
    public string? tierPosition { get; set; }
    public CommitStat[] stats { get; set; } = Array.Empty<CommitStat>();
}

public class CommitStat
{
    public int statType { get; set; }
    public int statValue { get; set; }
}

public class RollbackRequest
{
    public int creatureEntry { get; set; }
}