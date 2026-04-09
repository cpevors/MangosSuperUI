using MangosSuperUI.Models;
using Dapper;

namespace MangosSuperUI.Services;

/// <summary>
/// Records all MangosSuperUI panel actions to the vmangos_admin.audit_log table.
/// Singleton — inject and call LogAsync() from any controller or hub.
/// </summary>
public class AuditService
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<AuditService> _logger;
    private readonly StateCaptureService _stateCapture;

    public AuditService(ConnectionFactory db, StateCaptureService stateCapture, ILogger<AuditService> logger)
    {
        _db = db;
        _stateCapture = stateCapture;
        _logger = logger;
    }

    /// <summary>
    /// Full lifecycle: capture state before → execute RA command → capture state after → log everything.
    /// Use this from controllers and hubs instead of manually calling LogCommandAsync().
    /// Returns (response, success) so callers can forward the result.
    /// </summary>
    public async Task<(string response, bool success)> ExecuteAndLogAsync(
        RaService raService,
        string command,
        string? operatorIp = null,
        string? operator_ = null,
        string? notes = null)
    {
        // Step 1: Capture state BEFORE the command
        CaptureResult? capture = null;
        try
        {
            capture = await _stateCapture.CaptureBeforeAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-capture failed for: {Command}", command);
        }

        // Step 2: Execute the RA command
        string response;
        bool success;
        try
        {
            response = await raService.SendCommandAsync(command);
            success = true;
        }
        catch (Exception ex)
        {
            response = ex.Message;
            success = false;
            notes = (notes != null ? notes + " | " : "") + "Exception: " + ex.GetType().Name;
        }

        // Step 3: Capture state AFTER the command (only if it succeeded and we have a before snapshot)
        string? stateAfter = null;
        if (success && capture != null)
        {
            try
            {
                // Small delay to let VMaNGOS process the change
                await Task.Delay(200);
                stateAfter = await _stateCapture.CaptureAfterAsync(capture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-capture failed for: {Command}", command);
            }
        }

        // Step 4: Log to audit trail with full state
        await LogCommandAsync(
            command, response, success,
            targetType: capture?.TargetType,
            targetName: capture?.TargetName,
            targetId: capture?.TargetId,
            stateBefore: capture?.StateBefore,
            stateAfter: stateAfter,
            isReversible: capture?.IsReversible ?? false,
            operator_: operator_,
            operatorIp: operatorIp,
            notes: notes);

        return (response, success);
    }

    /// <summary>
    /// Log an action to the audit trail.
    /// </summary>
    public async Task<long> LogAsync(AuditEntry entry)
    {
        try
        {
            using var conn = _db.Admin();
            var id = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO audit_log 
                    (operator, operator_ip, category, action, target_type, target_name, target_id,
                     ra_command, ra_response, state_before, state_after, is_reversible, reverses_id, success, notes)
                  VALUES 
                    (@Operator, @OperatorIp, @Category, @Action, @TargetType, @TargetName, @TargetId,
                     @RaCommand, @RaResponse, @StateBefore, @StateAfter, @IsReversible, @ReversesId, @Success, @Notes);
                  SELECT LAST_INSERT_ID();",
                entry);

            return id;
        }
        catch (Exception ex)
        {
            // Audit logging should never crash the app — log and continue
            _logger.LogError(ex, "Failed to write audit log: {Category}/{Action} on {Target}",
                entry.Category, entry.Action, entry.TargetName);
            return 0;
        }
    }

    /// <summary>
    /// Convenience: log an RA command with its response.
    /// </summary>
    public async Task<long> LogCommandAsync(
        string command,
        string? response,
        bool success,
        string? targetType = null,
        string? targetName = null,
        int? targetId = null,
        string? stateBefore = null,
        string? stateAfter = null,
        bool isReversible = false,
        string? operator_ = null,
        string? operatorIp = null,
        string? notes = null)
    {
        var category = CategorizeCommand(command);
        var action = ActionFromCommand(command);

        return await LogAsync(new AuditEntry
        {
            Operator = operator_ ?? "admin",
            OperatorIp = operatorIp,
            Category = category,
            Action = action,
            TargetType = targetType,
            TargetName = targetName,
            TargetId = targetId,
            RaCommand = command,
            RaResponse = response,
            StateBefore = stateBefore,
            StateAfter = stateAfter,
            IsReversible = isReversible,
            Success = success,
            Notes = notes
        });
    }

    /// <summary>
    /// Log a config change with before/after JSON.
    /// </summary>
    public async Task LogConfigChangeAsync(string configJson, string? changesJson, string? operator_ = null)
    {
        try
        {
            using var conn = _db.Admin();
            await conn.ExecuteAsync(
                @"INSERT INTO config_history (operator, config_json, changes)
                  VALUES (@Operator, @ConfigJson, @Changes)",
                new { Operator = operator_ ?? "admin", ConfigJson = configJson, Changes = changesJson });

            await LogAsync(new AuditEntry
            {
                Operator = operator_ ?? "admin",
                Category = "config",
                Action = "save_config",
                TargetType = "config",
                TargetName = "server-config.json",
                StateAfter = changesJson,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log config change");
        }
    }

    /// <summary>
    /// Get recent audit log entries.
    /// </summary>
    public async Task<IEnumerable<AuditLogRow>> GetRecentAsync(int count = 50, string? category = null)
    {
        try
        {
            using var conn = _db.Admin();
            var sql = @"SELECT id, timestamp, operator, operator_ip AS operatorIp, 
                               category, action, target_type AS targetType, target_name AS targetName, 
                               target_id AS targetId, ra_command AS raCommand, ra_response AS raResponse,
                               state_before AS stateBefore, state_after AS stateAfter,
                               is_reversible AS isReversible, reverses_id AS reversesId, success, notes
                        FROM audit_log";

            if (!string.IsNullOrEmpty(category))
                sql += " WHERE category = @category";

            sql += " ORDER BY id DESC LIMIT @count";

            return await conn.QueryAsync<AuditLogRow>(sql, new { count, category });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query audit log");
            return Enumerable.Empty<AuditLogRow>();
        }
    }

    /// <summary>
    /// Get audit history for a specific target.
    /// </summary>
    public async Task<IEnumerable<AuditLogRow>> GetTargetHistoryAsync(string targetType, string targetName, int count = 50)
    {
        try
        {
            using var conn = _db.Admin();
            return await conn.QueryAsync<AuditLogRow>(
                @"SELECT id, timestamp, operator, operator_ip AS operatorIp,
                         category, action, target_type AS targetType, target_name AS targetName,
                         target_id AS targetId, ra_command AS raCommand, ra_response AS raResponse,
                         state_before AS stateBefore, state_after AS stateAfter,
                         is_reversible AS isReversible, reverses_id AS reversesId, success, notes
                  FROM audit_log
                  WHERE target_type = @targetType AND target_name = @targetName
                  ORDER BY id DESC LIMIT @count",
                new { targetType, targetName, count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query target history: {Type}/{Name}", targetType, targetName);
            return Enumerable.Empty<AuditLogRow>();
        }
    }

    // ==================== Helpers ====================

    private static string CategorizeCommand(string command)
    {
        var cmd = command.TrimStart('.').ToLower();
        if (cmd.StartsWith("account")) return "account";
        if (cmd.StartsWith("character") || cmd.StartsWith("reset")) return "character";
        if (cmd.StartsWith("ban") || cmd.StartsWith("unban")) return "ban";
        if (cmd.StartsWith("guild")) return "guild";
        if (cmd.StartsWith("server") || cmd.StartsWith("saveall") || cmd.StartsWith("reload")) return "system";
        if (cmd.StartsWith("bot") || cmd.StartsWith("ahbot") || cmd.StartsWith("battlebot")) return "bot";
        if (cmd.StartsWith("send") || cmd.StartsWith("kick") || cmd.StartsWith("mute") || cmd.StartsWith("unmute")) return "character";
        if (cmd.StartsWith("tele")) return "character";
        if (cmd.StartsWith("antispam") || cmd.StartsWith("spamer")) return "system";
        if (cmd.StartsWith("lookup") || cmd.StartsWith("spell") || cmd.StartsWith("list")) return "query";
        return "command";
    }

    private static string ActionFromCommand(string command)
    {
        var parts = command.TrimStart('.').Split(' ', 3);
        if (parts.Length >= 2)
            return (parts[0] + "_" + parts[1]).ToLower().Replace(".", "_");
        return parts[0].ToLower();
    }
}

// ==================== DTOs ====================

public class AuditEntry
{
    public string Operator { get; set; } = "admin";
    public string? OperatorIp { get; set; }
    public string Category { get; set; } = "command";
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetName { get; set; }
    public int? TargetId { get; set; }
    public string? RaCommand { get; set; }
    public string? RaResponse { get; set; }
    public string? StateBefore { get; set; }
    public string? StateAfter { get; set; }
    public bool IsReversible { get; set; }
    public long? ReversesId { get; set; }
    public bool Success { get; set; } = true;
    public string? Notes { get; set; }
}

public class AuditLogRow
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Operator { get; set; } = "";
    public string? OperatorIp { get; set; }
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetName { get; set; }
    public int? TargetId { get; set; }
    public string? RaCommand { get; set; }
    public string? RaResponse { get; set; }
    public string? StateBefore { get; set; }
    public string? StateAfter { get; set; }
    public bool IsReversible { get; set; }
    public long? ReversesId { get; set; }
    public bool Success { get; set; }
    public string? Notes { get; set; }
}