using MangosSuperUI.Services;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

/// <summary>
/// API endpoints for DBC lookups — resolves item display IDs and spell icon IDs
/// to icon file paths, and provides auxiliary spell data (durations, cast times, ranges).
/// Used by the spell/item/game object browsers and editors in Tier 3.
/// </summary>
public class DbcController : Controller
{
    private readonly DbcService _dbc;

    public DbcController(DbcService dbc)
    {
        _dbc = dbc;
    }

    // ── Status ────────────────────────────────────────────────────────────

    /// <summary>GET /Dbc/Status — diagnostics for the Settings page.</summary>
    [HttpGet]
    public IActionResult Status()
    {
        return Json(new
        {
            isLoaded = _dbc.IsLoaded,
            error = _dbc.LoadError,
            dbcPath = _dbc.DbcPath,
            counts = _dbc.LoadedCounts
        });
    }

    // ── Icon resolution ───────────────────────────────────────────────────

    /// <summary>GET /Dbc/ItemIcon?displayId=1234 — returns icon path for an item display ID.</summary>
    [HttpGet]
    public IActionResult ItemIcon(uint displayId)
    {
        return Json(new { iconPath = _dbc.GetItemIconPath(displayId) });
    }

    /// <summary>GET /Dbc/SpellIcon?spellIconId=1234 — returns icon path for a spell icon ID.</summary>
    [HttpGet]
    public IActionResult SpellIcon(uint spellIconId)
    {
        return Json(new { iconPath = _dbc.GetSpellIconPath(spellIconId) });
    }

    /// <summary>GET /Dbc/ItemIcons?displayIds=1,2,3 — batch resolve multiple item display IDs.</summary>
    [HttpGet]
    public IActionResult ItemIcons(string displayIds)
    {
        if (string.IsNullOrWhiteSpace(displayIds))
            return Json(new Dictionary<string, string>());

        var result = new Dictionary<string, string>();
        foreach (var part in displayIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part.Trim(), out uint id))
                result[id.ToString()] = _dbc.GetItemIconPath(id);
        }
        return Json(result);
    }

    /// <summary>GET /Dbc/SpellIcons?spellIconIds=1,2,3 — batch resolve multiple spell icon IDs.</summary>
    [HttpGet]
    public IActionResult SpellIcons(string spellIconIds)
    {
        if (string.IsNullOrWhiteSpace(spellIconIds))
            return Json(new Dictionary<string, string>());

        var result = new Dictionary<string, string>();
        foreach (var part in spellIconIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part.Trim(), out uint id))
                result[id.ToString()] = _dbc.GetSpellIconPath(id);
        }
        return Json(result);
    }

    // ── Auxiliary spell data ──────────────────────────────────────────────

    /// <summary>GET /Dbc/SpellDurations — all duration entries for dropdown population.</summary>
    [HttpGet]
    public IActionResult SpellDurations()
    {
        var result = _dbc.SpellDurations
            .OrderBy(kv => kv.Value.DurationMs)
            .Select(kv => new
            {
                id = kv.Key,
                durationMs = kv.Value.DurationMs,
                perLevel = kv.Value.DurationPerLevel,
                maxDurationMs = kv.Value.MaxDurationMs,
                label = kv.Value.DisplayLabel
            });
        return Json(result);
    }

    /// <summary>GET /Dbc/SpellCastTimes — all cast time entries for dropdown population.</summary>
    [HttpGet]
    public IActionResult SpellCastTimes()
    {
        var result = _dbc.SpellCastTimes
            .OrderBy(kv => kv.Value.BaseMs)
            .Select(kv => new
            {
                id = kv.Key,
                baseMs = kv.Value.BaseMs,
                perLevel = kv.Value.PerLevelMs,
                minimumMs = kv.Value.MinimumMs,
                label = kv.Value.DisplayLabel
            });
        return Json(result);
    }

    /// <summary>GET /Dbc/SpellRanges — all range entries for dropdown population.</summary>
    [HttpGet]
    public IActionResult SpellRanges()
    {
        var result = _dbc.SpellRanges
            .OrderBy(kv => kv.Value.RangeMax)
            .Select(kv => new
            {
                id = kv.Key,
                rangeMin = kv.Value.RangeMin,
                rangeMax = kv.Value.RangeMax,
                flags = kv.Value.Flags,
                name = kv.Value.DisplayName,
                label = kv.Value.DisplayLabel
            });
        return Json(result);
    }

    /// <summary>POST /Dbc/Reload — re-read all DBC files (after path change in Settings).</summary>
    [HttpPost]
    public IActionResult Reload()
    {
        _dbc.Reload();
        return Json(new
        {
            success = _dbc.IsLoaded,
            error = _dbc.LoadError,
            counts = _dbc.LoadedCounts
        });
    }
}