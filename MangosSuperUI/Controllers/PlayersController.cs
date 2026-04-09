using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class PlayersController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly RaService _raService;
    private readonly AuditService _audit;
    private readonly ILogger<PlayersController> _logger;

    public PlayersController(ConnectionFactory db, RaService raService, AuditService audit, ILogger<PlayersController> logger)
    {
        _db = db;
        _raService = raService;
        _audit = audit;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Autocomplete search — returns matching characters after 3+ chars typed.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 3)
            return Json(new object[0]);

        try
        {
            using var conn = _db.Characters();
            var results = await conn.QueryAsync<PlayerSearchResult>(
                @"SELECT c.guid, c.name, c.level, c.race, c.class AS classId, c.online,
                         a.username AS accountName
                  FROM characters c
                  JOIN realmd.account a ON a.id = c.account
                  WHERE c.name LIKE @term
                  ORDER BY c.name
                  LIMIT 15",
                new { term = q + "%" });

            return Json(results.Select(r => new
            {
                r.Guid,
                r.Name,
                r.Level,
                race = GetRaceName(r.Race),
                className = GetClassName(r.ClassId),
                r.Online,
                r.AccountName
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Player search failed for: {Query}", q);
            return Json(new object[0]);
        }
    }

    /// <summary>
    /// Full player detail by guid.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail([FromQuery] int guid)
    {
        try
        {
            using var charConn = _db.Characters();
            var player = await charConn.QueryFirstOrDefaultAsync<PlayerDetail>(
                @"SELECT c.guid, c.name, c.account, c.level, c.race, c.class AS classId,
                         c.gender, c.money, c.online, c.zone, c.map,
                         c.position_x AS posX, c.position_y AS posY, c.position_z AS posZ,
                         c.played_time_total AS playedTotal, c.played_time_level AS playedLevel,
                         c.create_time AS createTime, c.logout_time AS logoutTime,
                         c.honor_highest_rank AS highestRank, c.honor_rank_points AS rankPoints,
                         c.xp
                  FROM characters c
                  WHERE c.guid = @guid",
                new { guid });

            if (player == null)
                return Json(new { found = false });

            // Guild info
            var guildInfo = await charConn.QueryFirstOrDefaultAsync<GuildInfo>(
                @"SELECT g.name AS guildName, gm.rank AS guildRank
                  FROM guild_member gm
                  JOIN guild g ON g.guild_id = gm.guild_id
                  WHERE gm.guid = @guid",
                new { guid });

            // Account info from realmd
            using var realmdConn = _db.Realmd();
            var account = await realmdConn.QueryFirstOrDefaultAsync<AccountInfo>(
                @"SELECT id, username, last_ip AS lastIp, last_login AS lastLogin,
                         gmlevel AS gmLevel, locked, mutetime AS muteTime, online
                  FROM account WHERE id = @accountId",
                new { accountId = player.Account });

            return Json(new
            {
                found = true,
                player = new
                {
                    player.Guid,
                    player.Name,
                    player.Account,
                    player.Level,
                    race = GetRaceName(player.Race),
                    raceId = player.Race,
                    className = GetClassName(player.ClassId),
                    classId = player.ClassId,
                    gender = player.Gender == 0 ? "Male" : "Female",
                    gold = player.Money / 10000,
                    silver = (player.Money % 10000) / 100,
                    copper = player.Money % 100,
                    moneyRaw = player.Money,
                    player.Online,
                    player.Zone,
                    player.Map,
                    player.PosX,
                    player.PosY,
                    player.PosZ,
                    playedTotal = FormatPlaytime(player.PlayedTotal),
                    playedLevel = FormatPlaytime(player.PlayedLevel),
                    createTime = player.CreateTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(player.CreateTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—",
                    logoutTime = player.LogoutTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(player.LogoutTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—",
                    player.HighestRank,
                    player.RankPoints,
                    player.Xp
                },
                guild = guildInfo != null ? new { guildInfo.GuildName, guildInfo.GuildRank } : null,
                account = account != null ? new
                {
                    account.Id,
                    account.Username,
                    account.LastIp,
                    lastLogin = account.LastLogin.ToString("yyyy-MM-dd HH:mm"),
                    account.GmLevel,
                    gmLevelName = GetGmLevelName(account.GmLevel),
                    account.Locked,
                    isMuted = account.MuteTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    account.Online
                } : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load player detail for guid {Guid}", guid);
            return Json(new { found = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Send an RA command (player actions route through here).
    /// Now uses ExecuteAndLogAsync for automatic state capture.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RaCommand([FromBody] RaCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Command))
            return BadRequest(new { error = "Command cannot be empty" });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _raService, request.Command, operatorIp: ip, notes: "Player Actions");

        return Json(new { success, response = success ? response : null, error = success ? null : response });
    }

    // ==================== Helpers ====================

    private static string FormatPlaytime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static string GetGmLevelName(int level) => level switch
    {
        0 => "Player",
        1 => "Moderator",
        2 => "Ticket Master",
        3 => "Game Master",
        4 => "Basic Admin",
        5 => "Developer",
        6 => "Administrator",
        7 => "Console",
        _ => $"Unknown ({level})"
    };

    private static string GetRaceName(int race) => race switch
    {
        1 => "Human",
        2 => "Orc",
        3 => "Dwarf",
        4 => "Night Elf",
        5 => "Undead",
        6 => "Tauren",
        7 => "Gnome",
        8 => "Troll",
        _ => $"Unknown ({race})"
    };

    private static string GetClassName(int classId) => classId switch
    {
        1 => "Warrior",
        2 => "Paladin",
        3 => "Hunter",
        4 => "Rogue",
        5 => "Priest",
        7 => "Shaman",
        8 => "Mage",
        9 => "Warlock",
        11 => "Druid",
        _ => $"Unknown ({classId})"
    };
}

// ==================== DTOs ====================

public class PlayerSearchResult
{
    public int Guid { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int Race { get; set; }
    public int ClassId { get; set; }
    public int Online { get; set; }
    public string AccountName { get; set; } = "";
}

public class PlayerDetail
{
    public int Guid { get; set; }
    public string Name { get; set; } = "";
    public int Account { get; set; }
    public int Level { get; set; }
    public int Race { get; set; }
    public int ClassId { get; set; }
    public int Gender { get; set; }
    public long Money { get; set; }
    public int Online { get; set; }
    public int Zone { get; set; }
    public int Map { get; set; }
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public long PlayedTotal { get; set; }
    public long PlayedLevel { get; set; }
    public long CreateTime { get; set; }
    public long LogoutTime { get; set; }
    public int HighestRank { get; set; }
    public float RankPoints { get; set; }
    public int Xp { get; set; }
}

public class GuildInfo
{
    public string GuildName { get; set; } = "";
    public int GuildRank { get; set; }
}

public class AccountInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string LastIp { get; set; } = "";
    public DateTime LastLogin { get; set; }
    public int GmLevel { get; set; }
    public int Locked { get; set; }
    public long MuteTime { get; set; }
    public int Online { get; set; }
}

public class RaCommandRequest
{
    public string Command { get; set; } = "";
}