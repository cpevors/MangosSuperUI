using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Controllers;

public class ActivityController : Controller
{
    private readonly AuditService _audit;
    private readonly ConnectionFactory _db;
    private readonly ILogger<ActivityController> _logger;

    public ActivityController(AuditService audit, ConnectionFactory db, ILogger<ActivityController> logger)
    {
        _audit = audit;
        _db = db;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Paginated, filterable audit log entries.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Entries(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] bool? successOnly,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Admin();

            var where = "WHERE 1=1";
            var parameters = new DynamicParameters();

            if (!string.IsNullOrEmpty(category))
            {
                where += " AND category = @category";
                parameters.Add("category", category);
            }

            if (!string.IsNullOrEmpty(search))
            {
                where += " AND (target_name LIKE @search OR ra_command LIKE @search OR action LIKE @search OR notes LIKE @search)";
                parameters.Add("search", "%" + search + "%");
            }

            if (successOnly.HasValue)
            {
                where += " AND success = @success";
                parameters.Add("success", successOnly.Value ? 1 : 0);
            }

            // Total count
            var countSql = $"SELECT COUNT(*) FROM audit_log {where}";
            var total = await conn.ExecuteScalarAsync<int>(countSql, parameters);

            // Paged results
            var offset = (page - 1) * pageSize;
            parameters.Add("limit", pageSize);
            parameters.Add("offset", offset);

            var sql = $@"SELECT id, timestamp, operator, operator_ip AS operatorIp,
                                category, action, target_type AS targetType, target_name AS targetName,
                                target_id AS targetId, ra_command AS raCommand, ra_response AS raResponse,
                                state_before AS stateBefore, state_after AS stateAfter,
                                is_reversible AS isReversible, reverses_id AS reversesId, success, notes
                         FROM audit_log {where}
                         ORDER BY id DESC
                         LIMIT @limit OFFSET @offset";

            var entries = await conn.QueryAsync<AuditLogRow>(sql, parameters);

            return Json(new
            {
                entries,
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query audit log entries");
            return Json(new { entries = Array.Empty<object>(), total = 0, page, pageSize, totalPages = 0 });
        }
    }

    /// <summary>
    /// Category summary for filter chips (counts per category).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Summary()
    {
        try
        {
            using var conn = _db.Admin();

            var categories = await conn.QueryAsync<CategoryCount>(
                @"SELECT category, COUNT(*) AS count FROM audit_log GROUP BY category ORDER BY count DESC");

            var total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_log");

            var recentFailures = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_log WHERE success = 0 AND timestamp > DATE_SUB(NOW(), INTERVAL 24 HOUR)");

            var todayCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM audit_log WHERE timestamp > DATE_SUB(NOW(), INTERVAL 24 HOUR)");

            return Json(new { categories, total, recentFailures, todayCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query audit summary");
            return Json(new { categories = Array.Empty<object>(), total = 0, recentFailures = 0, todayCount = 0 });
        }
    }

    /// <summary>
    /// Single audit entry detail (for expanded card view).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Detail([FromQuery] long id)
    {
        try
        {
            using var conn = _db.Admin();
            var entry = await conn.QueryFirstOrDefaultAsync<AuditLogRow>(
                @"SELECT id, timestamp, operator, operator_ip AS operatorIp,
                         category, action, target_type AS targetType, target_name AS targetName,
                         target_id AS targetId, ra_command AS raCommand, ra_response AS raResponse,
                         state_before AS stateBefore, state_after AS stateAfter,
                         is_reversible AS isReversible, reverses_id AS reversesId, success, notes
                  FROM audit_log WHERE id = @id", new { id });

            return Json(new { found = entry != null, entry });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query audit detail for id {Id}", id);
            return Json(new { found = false });
        }
    }
}

public class CategoryCount
{
    public string Category { get; set; } = "";
    public int Count { get; set; }
}
