using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class HomeController : Controller
{
    private readonly ProcessManagerService _processManager;
    private readonly RaService _raService;
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly DbInitializationService _dbInit;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        ProcessManagerService processManager,
        RaService raService,
        ConnectionFactory db,
        AuditService audit,
        DbInitializationService dbInit,
        ILogger<HomeController> logger)
    {
        _processManager = processManager;
        _raService = raService;
        _db = db;
        _audit = audit;
        _dbInit = dbInit;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Status()
    {
        var mangosd = _processManager.GetMangosdStatus();
        var realmd = _processManager.GetRealmdStatus();

        // Parse .server info from RA for live data
        string serverInfoRaw = null;
        int playersOnline = 0;
        int maxOnline = 0;
        string uptime = null;
        string coreRevision = null;

        try
        {
            if (_raService.IsConnected || mangosd.IsRunning)
            {
                serverInfoRaw = await _raService.SendCommandAsync(".server info");
                ParseServerInfo(serverInfoRaw, out playersOnline, out maxOnline, out uptime, out coreRevision);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get .server info from RA");
        }

        // DB stats
        int totalAccounts = 0;
        int totalCharacters = 0;
        int gmAccounts = 0;
        int bannedAccounts = 0;

        try
        {
            using var realmdConn = _db.Realmd();
            totalAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account");
            gmAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT id) FROM account_access WHERE gmlevel > 0");
            bannedAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account_banned WHERE active = 1");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query realmd stats");
        }

        try
        {
            using var charConn = _db.Characters();
            totalCharacters = await charConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM characters");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query characters stats");
        }

        return Json(new
        {
            mangosd,
            realmd,
            raConnected = _raService.IsConnected,
            playersOnline,
            maxOnline,
            uptime,
            coreRevision,
            serverInfoRaw,
            totalAccounts,
            totalCharacters,
            gmAccounts,
            bannedAccounts
        });
    }

    /// <summary>
    /// Returns per-database connectivity and vmangos_admin init status.
    /// Polled once on dashboard load (not every 10s — this is heavier than Status).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> DbHealth()
    {
        var report = await _dbInit.CheckHealthAsync();
        return Json(report);
    }

    [HttpPost]
    public async Task<IActionResult> SendCommand([FromBody] CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Command))
            return BadRequest(new { error = "Command cannot be empty" });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _raService, request.Command, operatorIp: ip);

        return Json(new { success, response = success ? response : null, error = success ? null : response });
    }

    [HttpPost]
    public async Task<IActionResult> ProcessAction([FromBody] ProcessActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Service) || string.IsNullOrWhiteSpace(request?.Action))
            return BadRequest(new { error = "Service and action are required" });

        try
        {
            var result = (request.Service.ToLower(), request.Action.ToLower()) switch
            {
                ("mangosd", "start") => await _processManager.StartMangosdAsync(),
                ("mangosd", "stop") => await _processManager.StopMangosdAsync(),
                ("mangosd", "restart") => await _processManager.RestartMangosdAsync(),
                ("realmd", "start") => await _processManager.StartRealmdAsync(),
                ("realmd", "stop") => await _processManager.StopRealmdAsync(),
                ("realmd", "restart") => await _processManager.RestartRealmdAsync(),
                _ => throw new ArgumentException($"Unknown service/action: {request.Service}/{request.Action}")
            };

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = $"process_{request.Action}",
                TargetType = "service",
                TargetName = request.Service,
                Success = true,
                Notes = $"systemctl {request.Action} {request.Service}"
            });

            return Json(new { success = true, message = $"{request.Service} {request.Action} completed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessAction failed: {Service}/{Action}", request.Service, request.Action);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Category = "system",
                Action = $"process_{request.Action}",
                TargetType = "service",
                TargetName = request.Service,
                Success = false,
                Notes = ex.Message
            });
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Parses VMaNGOS .server info response.
    /// </summary>
    private static void ParseServerInfo(string raw, out int playersOnline, out int maxOnline, out string uptime, out string coreRevision)
    {
        playersOnline = 0;
        maxOnline = 0;
        uptime = null;
        coreRevision = null;

        if (string.IsNullOrEmpty(raw))
            return;

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Core revision:"))
            {
                coreRevision = trimmed["Core revision:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Players online:"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(trimmed,
                    @"Players online:\s*(\d+).*Max online:\s*(\d+)");
                if (match.Success)
                {
                    int.TryParse(match.Groups[1].Value, out playersOnline);
                    int.TryParse(match.Groups[2].Value, out maxOnline);
                }
            }
            else if (trimmed.StartsWith("Server uptime:"))
            {
                uptime = trimmed["Server uptime:".Length..].Trim().TrimEnd('.');
            }
        }
    }
}

public class CommandRequest
{
    public string Command { get; set; } = "";
}

public class ProcessActionRequest
{
    public string Service { get; set; } = "";
    public string Action { get; set; } = "";
}