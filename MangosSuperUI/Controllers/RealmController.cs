using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class RealmController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly ILogger<RealmController> _logger;

    public RealmController(ConnectionFactory db, AuditService audit, ILogger<RealmController> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Get all realms from the realmlist table.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        try
        {
            using var conn = _db.Realmd();
            var realms = await conn.QueryAsync<RealmRow>(
                @"SELECT id, name, address, port, icon, realmflags AS realmFlags,
                         timezone, population,
                         realmbuilds AS realmBuilds
                  FROM realmlist
                  ORDER BY id");

            // Get online character counts per realm
            using var charConn = _db.Characters();
            var onlineCount = await charConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM characters WHERE online = 1");

            // Get total account count
            var totalAccounts = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account");
            var onlineAccounts = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account WHERE online = 1");

            return Json(new
            {
                realms = realms.Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.Address,
                    r.Port,
                    icon = r.Icon,
                    iconName = GetRealmTypeName(r.Icon),
                    r.RealmFlags,
                    flagNames = GetRealmFlagNames(r.RealmFlags),
                    r.Timezone,
                    timezoneName = GetTimezoneName(r.Timezone),
                    r.Population,
                    populationLabel = GetPopulationLabel(r.Population),
                    r.RealmBuilds
                }),
                stats = new
                {
                    onlinePlayers = onlineCount,
                    onlineAccounts,
                    totalAccounts
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load realm list");
            return Json(new { realms = Array.Empty<object>(), stats = new { onlinePlayers = 0, onlineAccounts = 0, totalAccounts = 0 } });
        }
    }

    /// <summary>
    /// Update a realm's editable fields.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Update([FromBody] RealmUpdateRequest request)
    {
        if (request == null || request.Id <= 0)
            return BadRequest(new { error = "Invalid realm ID" });

        try
        {
            using var conn = _db.Realmd();

            // Capture current state for audit
            var before = await conn.QueryFirstOrDefaultAsync<RealmRow>(
                @"SELECT id, name, address, port, icon, realmflags AS realmFlags,
                         timezone, population, realmbuilds AS realmBuilds
                  FROM realmlist WHERE id = @Id", new { request.Id });

            if (before == null)
                return NotFound(new { error = "Realm not found" });

            await conn.ExecuteAsync(
                @"UPDATE realmlist SET 
                    name = @Name, address = @Address, port = @Port,
                    icon = @Icon, realmflags = @RealmFlags,
                    timezone = @Timezone, population = @Population
                  WHERE id = @Id",
                new
                {
                    request.Id,
                    request.Name,
                    request.Address,
                    request.Port,
                    request.Icon,
                    request.RealmFlags,
                    request.Timezone,
                    request.Population
                });

            // Audit the change
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var changes = new Dictionary<string, object>();
            if (before.Name != request.Name) changes["name"] = new { from = before.Name, to = request.Name };
            if (before.Address != request.Address) changes["address"] = new { from = before.Address, to = request.Address };
            if (before.Port != request.Port) changes["port"] = new { from = before.Port, to = request.Port };
            if (before.Icon != request.Icon) changes["icon"] = new { from = before.Icon, to = request.Icon };
            if (before.RealmFlags != request.RealmFlags) changes["realmflags"] = new { from = before.RealmFlags, to = request.RealmFlags };
            if (before.Timezone != request.Timezone) changes["timezone"] = new { from = before.Timezone, to = request.Timezone };
            if (Math.Abs(before.Population - request.Population) > 0.001) changes["population"] = new { from = before.Population, to = request.Population };

            if (changes.Count > 0)
            {
                await _audit.LogAsync(new AuditEntry
                {
                    Operator = "admin",
                    OperatorIp = ip,
                    Category = "system",
                    Action = "realm_update",
                    TargetType = "realm",
                    TargetName = request.Name,
                    TargetId = request.Id,
                    StateBefore = System.Text.Json.JsonSerializer.Serialize(before),
                    StateAfter = System.Text.Json.JsonSerializer.Serialize(changes),
                    Success = true,
                    Notes = "Realm configuration updated"
                });
            }

            return Json(new { success = true, changesCount = changes.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update realm {Id}", request.Id);
            return Json(new { success = false, error = ex.Message });
        }
    }

    // ==================== Helpers ====================

    private static string GetRealmTypeName(int icon) => icon switch
    {
        0 => "Normal",
        1 => "PvP",
        4 => "Normal (4)",
        6 => "RP",
        8 => "RP-PvP",
        _ => $"Unknown ({icon})"
    };

    private static string GetTimezoneName(int tz) => tz switch
    {
        0 => "Any",
        1 => "US — Development",
        2 => "US — English",
        3 => "US — Oceanic",
        4 => "US — Latin America",
        5 => "US — Tournament",
        6 => "KR — Korean",
        7 => "EU — Tournament",
        8 => "EU — English",
        9 => "EU — German",
        10 => "EU — French",
        11 => "EU — Spanish",
        12 => "EU — Russian",
        13 => "TW — Tournament",
        14 => "TW — Taiwanese",
        16 => "CN — Chinese",
        26 => "US — Test",
        _ => $"Unknown ({tz})"
    };

    private static string GetPopulationLabel(float pop)
    {
        if (pop <= 0) return "Offline";
        if (pop < 0.5) return "Low";
        if (pop < 1.0) return "Medium";
        if (pop < 2.0) return "High";
        return "Full";
    }

    private static List<string> GetRealmFlagNames(int flags)
    {
        var names = new List<string>();
        if ((flags & 0x01) != 0) names.Add("Version Mismatch");
        if ((flags & 0x02) != 0) names.Add("Offline");
        if ((flags & 0x04) != 0) names.Add("Specify Build");
        if ((flags & 0x20) != 0) names.Add("New Players");
        if ((flags & 0x40) != 0) names.Add("Recommended");
        if (names.Count == 0) names.Add("None");
        return names;
    }
}

// ==================== DTOs ====================

public class RealmRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public int Icon { get; set; }
    public int RealmFlags { get; set; }
    public int Timezone { get; set; }
    public float Population { get; set; }
    public string? RealmBuilds { get; set; }
}

public class RealmUpdateRequest
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public int Icon { get; set; }
    public int RealmFlags { get; set; }
    public int Timezone { get; set; }
    public float Population { get; set; }
}
