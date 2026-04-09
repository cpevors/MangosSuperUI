using MangosSuperUI.Models;
using Dapper;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MangosSuperUI.Services;

/// <summary>
/// Captures entity state before mutation commands for audit trail undo capability.
/// Parses RA commands to identify what entity is being changed, queries the DB
/// for current state, and returns structured JSON snapshots.
/// </summary>
public class StateCaptureService
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<StateCaptureService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public StateCaptureService(ConnectionFactory db, ILogger<StateCaptureService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Attempt to parse a command and snapshot the target entity's state before mutation.
    /// Returns null if the command is not a recognized mutation or the target can't be found.
    /// </summary>
    public async Task<CaptureResult?> CaptureBeforeAsync(string command)
    {
        try
        {
            var parsed = ParseCommand(command);
            if (parsed == null) return null;

            var state = await SnapshotAsync(parsed);
            if (state == null) return null;

            return new CaptureResult
            {
                TargetType = parsed.TargetType,
                TargetName = parsed.TargetName,
                TargetId = state.TargetId,
                StateBefore = state.Json,
                IsReversible = parsed.IsReversible,
                ReverseCommandTemplate = parsed.ReverseCommandTemplate
            };
        }
        catch (Exception ex)
        {
            // State capture should never block the actual command
            _logger.LogWarning(ex, "State capture failed for command: {Command}", command);
            return null;
        }
    }

    /// <summary>
    /// After a successful mutation, snapshot the new state for the same target.
    /// </summary>
    public async Task<string?> CaptureAfterAsync(CaptureResult? before)
    {
        if (before == null) return null;

        try
        {
            var parsed = new ParsedCommand
            {
                TargetType = before.TargetType,
                TargetName = before.TargetName
            };

            var state = await SnapshotAsync(parsed);
            return state?.Json;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-state capture failed for {Type}/{Name}", before.TargetType, before.TargetName);
            return null;
        }
    }

    // ==================== Command Parsing ====================

    private static ParsedCommand? ParseCommand(string command)
    {
        var cmd = command.Trim();
        if (!cmd.StartsWith('.')) return null;

        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        var root = parts[0].ToLower();
        var sub = parts.Length >= 2 ? parts[1].ToLower() : "";
        var full = root + " " + sub;

        return full switch
        {
            // Character mutations
            ".character level" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = true,
                ReverseCommandTemplate = ".character level {name} {before.level}"
            },
            ".character rename" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false // can't un-rename
            },

            // Resets
            ".reset talents" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },
            ".reset spells" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },
            ".reset all" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },

            // Communication
            ".kick" when parts.Length >= 2 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[1],
                IsReversible = false
            },
            ".mute" when parts.Length >= 2 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[1],
                IsReversible = true,
                ReverseCommandTemplate = ".unmute {name}"
            },
            ".unmute" when parts.Length >= 2 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[1],
                IsReversible = false
            },

            // Account mutations
            ".account set" when parts.Length >= 4 => ParseAccountSet(parts),

            // Bans
            ".ban account" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "account",
                TargetName = parts[2],
                IsReversible = true,
                ReverseCommandTemplate = ".unban account {name}"
            },
            ".ban character" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = true,
                ReverseCommandTemplate = ".unban character {name}"
            },
            ".unban account" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "account",
                TargetName = parts[2],
                IsReversible = false
            },
            ".unban character" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },

            // Send (mail/money/items) — snapshot the target player
            ".send money" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },
            ".send items" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },
            ".send mail" when parts.Length >= 3 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[2],
                IsReversible = false
            },

            // Revive / Repair — snapshot character
            ".revive" when parts.Length >= 2 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[1],
                IsReversible = false
            },
            ".repairitems" when parts.Length >= 2 => new ParsedCommand
            {
                TargetType = "character",
                TargetName = parts[1],
                IsReversible = false
            },

            _ => null
        };
    }

    private static ParsedCommand? ParseAccountSet(string[] parts)
    {
        // .account set gmlevel <name> <level>
        // .account set password <name> <pass> <pass>
        if (parts.Length < 5) return null;

        var subCmd = parts[2].ToLower();
        return subCmd switch
        {
            "gmlevel" => new ParsedCommand
            {
                TargetType = "account",
                TargetName = parts[3],
                IsReversible = true,
                ReverseCommandTemplate = ".account set gmlevel {name} {before.gmLevel}"
            },
            "password" => new ParsedCommand
            {
                TargetType = "account",
                TargetName = parts[3],
                IsReversible = false // can't un-change a password
            },
            _ => null
        };
    }

    // ==================== State Snapshots ====================

    private async Task<SnapshotResult?> SnapshotAsync(ParsedCommand parsed)
    {
        return parsed.TargetType switch
        {
            "character" => await SnapshotCharacterAsync(parsed.TargetName),
            "account" => await SnapshotAccountAsync(parsed.TargetName),
            _ => null
        };
    }

    private async Task<SnapshotResult?> SnapshotCharacterAsync(string name)
    {
        using var conn = _db.Characters();
        var row = await conn.QueryFirstOrDefaultAsync(
            @"SELECT c.guid, c.name, c.level, c.race, c.class AS classId, c.gender,
                     c.money, c.online, c.zone, c.map, c.xp,
                     c.honor_highest_rank AS highestRank, c.honor_rank_points AS rankPoints
              FROM characters c
              WHERE c.name = @name",
            new { name });

        if (row == null) return null;

        // Also check mute status from account
        int? muteTime = null;
        try
        {
            using var realmdConn = _db.Realmd();
            var accountId = await conn.ExecuteScalarAsync<int?>(
                "SELECT account FROM characters WHERE name = @name", new { name });

            if (accountId.HasValue)
            {
                muteTime = await realmdConn.ExecuteScalarAsync<int?>(
                    "SELECT mutetime FROM account WHERE id = @id", new { id = accountId.Value });
            }
        }
        catch { /* non-critical */ }

        var state = new
        {
            guid = (int)row.guid,
            name = (string)row.name,
            level = (int)row.level,
            race = (int)row.race,
            classId = (int)row.classId,
            gender = (int)row.gender,
            money = (long)row.money,
            online = (int)row.online,
            zone = (int)row.zone,
            map = (int)row.map,
            xp = (int)row.xp,
            highestRank = (int)row.highestRank,
            rankPoints = (float)row.rankPoints,
            muteTime
        };

        return new SnapshotResult
        {
            TargetId = (int)row.guid,
            Json = JsonSerializer.Serialize(state, JsonOpts)
        };
    }

    private async Task<SnapshotResult?> SnapshotAccountAsync(string username)
    {
        using var conn = _db.Realmd();
        var row = await conn.QueryFirstOrDefaultAsync(
            @"SELECT a.id, a.username, a.locked, a.mutetime AS muteTime, a.online
              FROM account a
              WHERE a.username = @username",
            new { username });

        if (row == null) return null;

        // GM level
        int gmLevel = 0;
        try
        {
            gmLevel = await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(gmlevel), 0) FROM account_access WHERE id = @id",
                new { id = (int)row.id });
        }
        catch { /* non-critical */ }

        // Ban status
        bool isBanned = false;
        try
        {
            isBanned = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM account_banned WHERE id = @id AND active = 1",
                new { id = (int)row.id }) > 0;
        }
        catch { /* non-critical */ }

        var state = new
        {
            id = (int)row.id,
            username = (string)row.username,
            gmLevel,
            locked = (int)row.locked,
            muteTime = (long)row.muteTime,
            online = (int)row.online,
            isBanned
        };

        return new SnapshotResult
        {
            TargetId = (int)row.id,
            Json = JsonSerializer.Serialize(state, JsonOpts)
        };
    }
}

// ==================== DTOs ====================

public class ParsedCommand
{
    public string TargetType { get; set; } = "";
    public string TargetName { get; set; } = "";
    public bool IsReversible { get; set; }
    public string? ReverseCommandTemplate { get; set; }
}

public class CaptureResult
{
    public string TargetType { get; set; } = "";
    public string TargetName { get; set; } = "";
    public int? TargetId { get; set; }
    public string? StateBefore { get; set; }
    public string? StateAfter { get; set; }
    public bool IsReversible { get; set; }
    public string? ReverseCommandTemplate { get; set; }
}

public class SnapshotResult
{
    public int TargetId { get; set; }
    public string Json { get; set; } = "";
}
