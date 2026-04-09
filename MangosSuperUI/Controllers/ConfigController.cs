using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using MangosSuperUI.Models;
using Dapper;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Controllers;

public class ConfigController : Controller
{
    private readonly RaService _raService;
    private readonly AuditService _audit;
    private readonly VmangosSettings _vmangosSettings;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(RaService raService, AuditService audit,
        IOptions<VmangosSettings> vmangosSettings, ILogger<ConfigController> logger)
    {
        _raService = raService;
        _audit = audit;
        _vmangosSettings = vmangosSettings.Value;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Read and parse the entire mangosd.conf file.
    /// Returns structured sections with settings, comments, and line numbers.
    /// </summary>
    [HttpGet]
    public IActionResult Load()
    {
        try
        {
            var confPath = GetConfPath();
            if (!System.IO.File.Exists(confPath))
                return Json(new { success = false, error = $"Config file not found: {confPath}" });

            var lines = System.IO.File.ReadAllLines(confPath);
            var settings = new List<ConfigSetting>();
            var currentComment = new List<string>();
            var currentSection = "General";

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();

                // Section headers like "# CHAT SETTINGS" or "###...### \n # SECTION NAME"
                if (trimmed.StartsWith("#") && !trimmed.StartsWith("#    ") && !trimmed.StartsWith("# "))
                {
                    // Major section header — lines like "# CHAT SETTINGS" or "# CONNECTIONS AND DIRECTORIES"
                    var sectionMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^#+\s*(.+?)\s*#*$");
                    if (sectionMatch.Success)
                    {
                        var candidate = sectionMatch.Groups[1].Value.Trim();
                        // Only treat as section if it looks like a title (mostly uppercase words)
                        if (candidate.Length > 3 && candidate == candidate.ToUpper() && !candidate.Contains("="))
                        {
                            currentSection = TitleCase(candidate);
                            currentComment.Clear();
                            continue;
                        }
                    }
                }

                // Comment lines — accumulate for the next setting
                if (trimmed.StartsWith("#"))
                {
                    // Strip leading "# " or "#    " for tooltip
                    var commentText = trimmed.TrimStart('#').TrimStart();
                    if (!string.IsNullOrWhiteSpace(commentText) && !commentText.All(c => c == '#' || c == '-' || c == '='))
                    {
                        currentComment.Add(commentText);
                    }
                    continue;
                }

                // Empty lines — reset comment accumulator
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // Keep comments if next line might be a setting
                    continue;
                }

                // Setting line: Key = Value
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx > 0)
                {
                    var key = trimmed[..eqIdx].Trim();
                    var value = trimmed[(eqIdx + 1)..].Trim();

                    // Remove surrounding quotes from string values
                    var rawValue = value;
                    var isQuoted = value.StartsWith('"') && value.EndsWith('"');
                    if (isQuoted)
                        value = value[1..^1];

                    settings.Add(new ConfigSetting
                    {
                        Line = i + 1,
                        Key = key,
                        Value = value,
                        RawValue = rawValue,
                        IsQuoted = isQuoted,
                        Section = currentSection,
                        Description = currentComment.Count > 0 ? string.Join(" ", currentComment) : null
                    });

                    currentComment.Clear();
                }
            }

            // Build section summary
            var sections = settings
                .GroupBy(s => s.Section)
                .Select(g => new { name = g.Key, count = g.Count() })
                .ToList();

            return Json(new
            {
                success = true,
                path = confPath,
                totalLines = lines.Length,
                totalSettings = settings.Count,
                sections,
                settings = settings.Select(s => new
                {
                    s.Line,
                    s.Key,
                    s.Value,
                    s.IsQuoted,
                    s.Section,
                    s.Description
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load mangosd.conf");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Update one or more settings in the conf file.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] ConfigSaveRequest request)
    {
        if (request?.Changes == null || request.Changes.Count == 0)
            return BadRequest(new { error = "No changes provided" });

        try
        {
            var confPath = GetConfPath();
            if (!System.IO.File.Exists(confPath))
                return Json(new { success = false, error = $"Config file not found: {confPath}" });

            // Create backup
            var backupPath = confPath + ".bak." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            System.IO.File.Copy(confPath, backupPath, overwrite: true);

            var lines = System.IO.File.ReadAllLines(confPath);
            var appliedChanges = new Dictionary<string, ConfigChange>();

            foreach (var change in request.Changes)
            {
                bool found = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("#")) continue;
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx <= 0) continue;

                    var key = trimmed[..eqIdx].Trim();
                    if (!key.Equals(change.Key, StringComparison.OrdinalIgnoreCase)) continue;

                    // Found the line — extract old value and build new line
                    var oldValue = trimmed[(eqIdx + 1)..].Trim();
                    var isQuoted = oldValue.StartsWith('"') && oldValue.EndsWith('"');
                    var cleanOld = isQuoted ? oldValue[1..^1] : oldValue;

                    string newRawValue;
                    if (isQuoted || change.ForceQuote)
                        newRawValue = $"\"{change.Value}\"";
                    else
                        newRawValue = change.Value;

                    // Preserve indentation/alignment: rebuild the line
                    // Find the whitespace pattern around the equals sign
                    var originalLine = lines[i];
                    var keyEnd = originalLine.IndexOf(key) + key.Length;
                    var eqInOriginal = originalLine.IndexOf('=', keyEnd);
                    var prefix = originalLine[..(eqInOriginal + 1)];
                    // Check if there's a space after =
                    var afterEq = eqInOriginal + 1 < originalLine.Length && originalLine[eqInOriginal + 1] == ' ' ? " " : "";

                    lines[i] = prefix + afterEq + newRawValue;

                    appliedChanges[change.Key] = new ConfigChange
                    {
                        Key = change.Key,
                        OldValue = cleanOld,
                        NewValue = change.Value,
                        Line = i + 1
                    };

                    found = true;
                    break;
                }

                if (!found)
                {
                    _logger.LogWarning("Config key not found: {Key}", change.Key);
                }
            }

            // Write the file
            await System.IO.File.WriteAllLinesAsync(confPath, lines);

            // Audit log
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var changesJson = System.Text.Json.JsonSerializer.Serialize(
                appliedChanges.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new { from = kvp.Value.OldValue, to = kvp.Value.NewValue }
                ));

            await _audit.LogAsync(new AuditEntry
            {
                Operator = "admin",
                OperatorIp = ip,
                Category = "config",
                Action = "mangosd_conf_update",
                TargetType = "config",
                TargetName = "mangosd.conf",
                StateAfter = changesJson,
                IsReversible = true,
                Success = true,
                Notes = $"Updated {appliedChanges.Count} setting(s). Backup: {Path.GetFileName(backupPath)}"
            });

            return Json(new
            {
                success = true,
                appliedCount = appliedChanges.Count,
                notFound = request.Changes.Count - appliedChanges.Count,
                backupFile = Path.GetFileName(backupPath),
                changes = appliedChanges.Values.Select(c => new
                {
                    c.Key,
                    c.OldValue,
                    c.NewValue,
                    c.Line
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save mangosd.conf");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Send .reload config to the server via RA.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Reload()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _raService, ".reload config", operatorIp: ip, notes: "Config Editor — reload after edit");

        return Json(new { success, response });
    }

    // ==================== Helpers ====================

    private string GetConfPath()
    {
        // Check for override in settings, otherwise use default
        var path = _vmangosSettings.MangosdConfPath;
        if (string.IsNullOrWhiteSpace(path))
            path = "/home/wowvmangos/vmangos/run/etc/mangosd.conf";
        return path;
    }

    private static string TitleCase(string upper)
    {
        if (string.IsNullOrEmpty(upper)) return upper;
        var words = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w =>
            w.Length <= 3
                ? w
                : char.ToUpper(w[0]) + w[1..].ToLower()
        ));
    }
}

// ==================== DTOs ====================

public class ConfigSetting
{
    public int Line { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string RawValue { get; set; } = "";
    public bool IsQuoted { get; set; }
    public string Section { get; set; } = "";
    public string? Description { get; set; }
}

public class ConfigSaveRequest
{
    public List<ConfigChangeRequest> Changes { get; set; } = new();
}

public class ConfigChangeRequest
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool ForceQuote { get; set; }
}

public class ConfigChange
{
    public string Key { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public int Line { get; set; }
}
