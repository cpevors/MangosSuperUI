using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class ServerLogsController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<ServerLogsController> _logger;

    public ServerLogsController(ConnectionFactory db, ILogger<ServerLogsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Character events: logins, logouts, creates, deletes, lost sockets.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Characters(
        [FromQuery] string? type,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();
            var where = "WHERE 1=1";
            var p = new DynamicParameters();

            if (!string.IsNullOrEmpty(type))
            {
                where += " AND type = @type";
                p.Add("type", type);
            }
            if (!string.IsNullOrEmpty(search))
            {
                where += " AND (name LIKE @search OR ip LIKE @search)";
                p.Add("search", "%" + search + "%");
            }

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM logs_characters {where}", p);

            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                $@"SELECT time, type, guid, account, name, ip, clientHash
                   FROM logs_characters {where}
                   ORDER BY time DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_characters");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Chat messages: say, whisper, group, guild, officer, raid, BG, channel.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Chat(
        [FromQuery] string? type,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();
            var where = "WHERE 1=1";
            var p = new DynamicParameters();

            if (!string.IsNullOrEmpty(type))
            {
                where += " AND type = @type";
                p.Add("type", type);
            }
            if (!string.IsNullOrEmpty(search))
            {
                where += " AND (message LIKE @search OR channelName LIKE @search)";
                p.Add("search", "%" + search + "%");
            }

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM logs_chat {where}", p);
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                $@"SELECT time, type, guid, target, channelId, channelName, message
                   FROM logs_chat {where}
                   ORDER BY time DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_chat");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Trade/economy logs: auction, mail, loot, quest, GM, etc.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Trades(
        [FromQuery] string? type,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();
            var where = "WHERE 1=1";
            var p = new DynamicParameters();

            if (!string.IsNullOrEmpty(type))
            {
                where += " AND type = @type";
                p.Add("type", type);
            }

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM logs_trade {where}", p);
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                $@"SELECT time, type, sender, senderType, senderEntry, receiver, amount, data
                   FROM logs_trade {where}
                   ORDER BY time DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_trade");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Item/gold transactions: auction bids, buyouts, trades, mail, COD.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Transactions(
        [FromQuery] string? type,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();
            var where = "WHERE 1=1";
            var p = new DynamicParameters();

            if (!string.IsNullOrEmpty(type))
            {
                where += " AND type = @type";
                p.Add("type", type);
            }

            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM logs_transactions {where}", p);
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                $@"SELECT time, type, guid1, money1, spell1, items1, guid2, money2, spell2, items2
                   FROM logs_transactions {where}
                   ORDER BY time DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_transactions");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Warden anticheat logs.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Warden(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();

            var total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logs_warden");
            var p = new DynamicParameters();
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                @"SELECT entry, `check`, action, account, guid, map, position_x AS posX, position_y AS posY, position_z AS posZ, date
                  FROM logs_warden ORDER BY date DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_warden");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Spam detection logs.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Spam(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();

            var total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logs_spamdetect");
            var p = new DynamicParameters();
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                @"SELECT time, accountId, guid, message, reason
                  FROM logs_spamdetect ORDER BY time DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_spamdetect");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Suspicious behavior detection logs.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Behavior(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();

            var total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logs_behavior");
            var p = new DynamicParameters();
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                @"SELECT id, account, detection, data
                  FROM logs_behavior ORDER BY id DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_behavior");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Battleground result logs.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Battlegrounds(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Logs();

            var total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM logs_battleground");
            var p = new DynamicParameters();
            p.Add("limit", pageSize);
            p.Add("offset", (page - 1) * pageSize);

            var rows = await conn.QueryAsync(
                @"SELECT time, bgid, bgtype, bgteamcount, bgduration, playerGuid, team, deaths, honorBonus, honorableKills
                  FROM logs_battleground ORDER BY time DESC LIMIT @limit OFFSET @offset", p);

            return Json(new { rows, total, page, pageSize, totalPages = (int)Math.Ceiling((double)total / pageSize) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query logs_battleground");
            return Json(new { rows = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Table row counts for the overview cards.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Overview()
    {
        try
        {
            using var conn = _db.Logs();

            var counts = new Dictionary<string, int>();
            var tables = new[] { "logs_characters", "logs_chat", "logs_trade", "logs_transactions",
                                 "logs_warden", "logs_spamdetect", "logs_behavior", "logs_battleground" };

            foreach (var table in tables)
            {
                try
                {
                    counts[table] = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{table}`");
                }
                catch
                {
                    counts[table] = -1; // table might not exist
                }
            }

            return Json(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query server logs overview");
            return Json(new Dictionary<string, int>());
        }
    }
}
