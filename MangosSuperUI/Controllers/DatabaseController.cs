using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Dapper;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class DatabaseController : Controller
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DatabaseController> _logger;

    // Loaded once at startup from wwwroot/data/curated-relationships.json
    private static JsonDocument? _relDoc;
    private static Dictionary<string, JsonElement>? _schemaMap;   // "mangos.creature_template" → schema object
    private static Dictionary<string, JsonElement>? _tablesMap;   // "mangos.creature_template" → table meta object
    private static List<JsonElement>? _edges;
    private static readonly object _loadLock = new();

    // Databases that are read-only (no INSERT/UPDATE/DELETE)
    private static readonly HashSet<string> READ_ONLY_DBS = new(StringComparer.OrdinalIgnoreCase) { "logs", "characters" };

    public DatabaseController(ConnectionFactory db, AuditService audit, IWebHostEnvironment env, ILogger<DatabaseController> logger)
    {
        _db = db;
        _audit = audit;
        _env = env;
        _logger = logger;
        EnsureRelationshipsLoaded();
    }

    public IActionResult Index() => View();

    // ===================== SCHEMA / RELATIONSHIPS LOADING =====================

    private void EnsureRelationshipsLoaded()
    {
        if (_relDoc != null) return;
        lock (_loadLock)
        {
            if (_relDoc != null) return;
            try
            {
                var path = Path.Combine(_env.WebRootPath, "data", "curated-relationships.json");
                var json = System.IO.File.ReadAllText(path);
                _relDoc = JsonDocument.Parse(json);

                // Build schema map
                _schemaMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in _relDoc.RootElement.GetProperty("schema").EnumerateObject())
                    _schemaMap[prop.Name] = prop.Value;

                // Build tables map
                _tablesMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in _relDoc.RootElement.GetProperty("tables").EnumerateObject())
                    _tablesMap[prop.Name] = prop.Value;

                // Build edges list
                _edges = new List<JsonElement>();
                foreach (var edge in _relDoc.RootElement.GetProperty("edges").EnumerateArray())
                    _edges.Add(edge);

                _logger.LogInformation("Loaded curated-relationships.json: {Tables} tables, {Edges} edges",
                    _schemaMap.Count, _edges.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load curated-relationships.json");
            }
        }
    }

    /// <summary>
    /// Validate that a database name is one of our known databases.
    /// </summary>
    private bool IsValidDatabase(string db)
        => db is "mangos" or "characters" or "realmd" or "logs";

    /// <summary>
    /// Validate a table exists in our schema map.
    /// </summary>
    private bool IsValidTable(string db, string table)
        => _schemaMap != null && _schemaMap.ContainsKey($"{db}.{table}");

    /// <summary>
    /// Validate a column exists for a given table in the schema.
    /// </summary>
    private bool IsValidColumn(string db, string table, string column)
    {
        if (_schemaMap == null) return false;
        var key = $"{db}.{table}";
        if (!_schemaMap.TryGetValue(key, out var schema)) return false;
        foreach (var col in schema.GetProperty("columns").EnumerateArray())
        {
            if (string.Equals(col.GetProperty("name").GetString(), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get the PK column names for a table from the schema.
    /// </summary>
    private List<string> GetPrimaryKeys(string db, string table)
    {
        var pks = new List<string>();
        if (_schemaMap == null) return pks;
        var key = $"{db}.{table}";
        if (!_schemaMap.TryGetValue(key, out var schema)) return pks;
        foreach (var col in schema.GetProperty("columns").EnumerateArray())
        {
            var colKey = col.GetProperty("key").GetString() ?? "";
            if (colKey == "PRI")
                pks.Add(col.GetProperty("name").GetString()!);
        }
        return pks;
    }

    /// <summary>
    /// Get a MySqlConnection for the given database name.
    /// </summary>
    private MySqlConnector.MySqlConnection GetConnection(string db) => db.ToLower() switch
    {
        "mangos" => _db.Mangos(),
        "characters" => _db.Characters(),
        "realmd" => _db.Realmd(),
        "logs" => _db.Logs(),
        _ => throw new ArgumentException($"Unknown database: {db}")
    };

    // ===================== API ENDPOINTS =====================

    /// <summary>
    /// GET /Database/Tables — all tables grouped by DB with edge counts.
    /// </summary>
    [HttpGet]
    public IActionResult Tables()
    {
        if (_tablesMap == null || _schemaMap == null)
            return StatusCode(500, new { error = "Relationship data not loaded" });

        var groups = new Dictionary<string, List<object>>();
        foreach (var kvp in _tablesMap)
        {
            var val = kvp.Value;
            var dbName = val.GetProperty("database").GetString()!;
            if (!groups.ContainsKey(dbName))
                groups[dbName] = new List<object>();

            groups[dbName].Add(new
            {
                table = val.GetProperty("table").GetString(),
                columns = val.GetProperty("columns").GetInt32(),
                estRows = val.GetProperty("est_rows").GetInt64(),
                totalEdges = val.GetProperty("total_edges").GetInt32()
            });
        }

        // Sort each group alphabetically
        foreach (var key in groups.Keys.ToList())
            groups[key] = groups[key].OrderBy(t => ((dynamic)t).table).ToList();

        return Json(new
        {
            databases = groups,
            totalTables = _tablesMap.Count,
            totalEdges = _edges?.Count ?? 0
        });
    }

    /// <summary>
    /// GET /Database/Schema/{db}/{table} — column metadata for one table.
    /// </summary>
    [HttpGet]
    [Route("Database/Schema/{db}/{table}")]
    public IActionResult Schema(string db, string table)
    {
        if (!IsValidDatabase(db) || !IsValidTable(db, table))
            return BadRequest(new { error = "Invalid database or table" });

        var key = $"{db}.{table}";
        var schema = _schemaMap![key];
        var pks = GetPrimaryKeys(db, table);
        var readOnly = READ_ONLY_DBS.Contains(db) || pks.Count == 0;

        return Json(new
        {
            database = db,
            table,
            columns = schema.GetProperty("columns"),
            indexes = schema.GetProperty("indexes"),
            primaryKeys = pks,
            readOnly,
            readOnlyReason = READ_ONLY_DBS.Contains(db)
                ? $"The {db} database is read-only"
                : pks.Count == 0 ? "Table has no primary key" : (string?)null
        });
    }

    /// <summary>
    /// GET /Database/Data/{db}/{table}?page=1&pageSize=50&sort=name&dir=asc&filterCol=entry&filterVal=1234
    /// Paginated data query with optional single-column filter.
    /// </summary>
    [HttpGet]
    [Route("Database/Data/{db}/{table}")]
    public async Task<IActionResult> Data(string db, string table, int page = 1, int pageSize = 50,
        string? sort = null, string? dir = null, string? filterCol = null, string? filterVal = null)
    {
        if (!IsValidDatabase(db) || !IsValidTable(db, table))
            return BadRequest(new { error = "Invalid database or table" });

        if (pageSize < 1) pageSize = 50;
        if (pageSize > 500) pageSize = 500;
        if (page < 1) page = 1;

        // Validate sort column
        if (!string.IsNullOrEmpty(sort) && !IsValidColumn(db, table, sort))
            sort = null;

        // Validate filter column
        // Single-column exact filter (from relationship navigation)
        bool hasSingleFilter = !string.IsNullOrEmpty(filterCol) && !string.IsNullOrEmpty(filterVal)
                         && IsValidColumn(db, table, filterCol);

        // All-columns search (no filterCol, just filterVal)
        bool hasGlobalSearch = !hasSingleFilter && !string.IsNullOrEmpty(filterVal) && string.IsNullOrEmpty(filterCol);

        var sortDir = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";

        try
        {
            using var conn = GetConnection(db);

            // Build WHERE clause
            var whereClause = "";
            var parameters = new DynamicParameters();
            if (hasSingleFilter)
            {
                whereClause = $"WHERE `{filterCol}` = @filterVal";
                parameters.Add("filterVal", filterVal);
            }
            else if (hasGlobalSearch)
            {
                // Search across all columns with LIKE
                var key = $"{db}.{table}";
                if (_schemaMap!.TryGetValue(key, out var schemaEl))
                {
                    var likeClauses = new List<string>();
                    foreach (var col in schemaEl.GetProperty("columns").EnumerateArray())
                    {
                        var colName = col.GetProperty("name").GetString()!;
                        likeClauses.Add($"`{colName}` LIKE @searchVal");
                    }
                    if (likeClauses.Count > 0)
                    {
                        whereClause = "WHERE (" + string.Join(" OR ", likeClauses) + ")";
                        parameters.Add("searchVal", $"%{filterVal}%");
                    }
                }
            }

            // Count
            var countSql = $"SELECT COUNT(*) FROM `{table}` {whereClause}";
            var totalRows = await conn.ExecuteScalarAsync<long>(countSql, parameters);

            // Determine sort
            var orderClause = !string.IsNullOrEmpty(sort)
                ? $"ORDER BY `{sort}` {sortDir}"
                : "";  // MySQL default ordering

            var offset = (page - 1) * pageSize;
            var dataSql = $"SELECT * FROM `{table}` {whereClause} {orderClause} LIMIT {pageSize} OFFSET {offset}";

            var rows = (await conn.QueryAsync(dataSql, parameters)).AsList();

            return Json(new
            {
                page,
                pageSize,
                totalRows,
                totalPages = (int)Math.Ceiling((double)totalRows / pageSize),
                rows
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data query failed: {Db}.{Table}", db, table);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Database/Relationships/{db}/{table}/{column}/{value}
    /// Returns all edges for a specific row value, with COUNT(*) per connected table.
    /// </summary>
    [HttpGet]
    [Route("Database/Relationships/{db}/{table}/{column}/{value}")]
    public async Task<IActionResult> Relationships(string db, string table, string column, string value)
    {
        if (!IsValidDatabase(db) || !IsValidTable(db, table))
            return BadRequest(new { error = "Invalid database or table" });
        if (!IsValidColumn(db, table, column))
            return BadRequest(new { error = $"Invalid column: {column}" });

        if (_edges == null) return Json(new { edges = Array.Empty<object>() });

        var fullTable = $"{db}.{table}";
        var fullCol = $"{fullTable}.{column}";

        // Find all edges where this table.column is the from or to side
        var results = new List<object>();

        foreach (var edge in _edges)
        {
            var from = edge.GetProperty("from").GetString()!;  // "mangos.creature_template.entry"
            var to = edge.GetProperty("to").GetString()!;
            var score = edge.GetProperty("score").GetDouble();
            var confidence = edge.GetProperty("confidence").GetString()!;

            string? targetDb = null, targetTable = null, targetCol = null;
            string direction;

            if (from == fullCol)
            {
                // Outbound: this column references another table
                var parts = to.Split('.');
                if (parts.Length != 3) continue;
                targetDb = parts[0]; targetTable = parts[1]; targetCol = parts[2];
                direction = "outbound";
            }
            else if (to == fullCol)
            {
                // Inbound: another table references this column
                var parts = from.Split('.');
                if (parts.Length != 3) continue;
                targetDb = parts[0]; targetTable = parts[1]; targetCol = parts[2];
                direction = "inbound";
            }
            else
            {
                continue;
            }

            // Run COUNT query against target table
            long count = 0;
            try
            {
                using var conn = GetConnection(targetDb);
                count = await conn.ExecuteScalarAsync<long>(
                    $"SELECT COUNT(*) FROM `{targetTable}` WHERE `{targetCol}` = @val",
                    new { val = value });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Count query failed for {Db}.{Table}.{Col}={Val}",
                    targetDb, targetTable, targetCol, value);
            }

            results.Add(new
            {
                targetDb,
                targetTable,
                targetCol,
                direction,
                count,
                score,
                confidence
            });
        }

        // Sort: highest count first, then by score
        results = results.OrderByDescending(r => ((dynamic)r).count)
                         .ThenByDescending(r => ((dynamic)r).score)
                         .ToList();

        return Json(new
        {
            sourceDb = db,
            sourceTable = table,
            sourceColumn = column,
            sourceValue = value,
            edges = results
        });
    }

    /// <summary>
    /// GET /Database/RelatedRows/{db}/{table}/{column}/{value}?limit=50
    /// Returns the actual rows from a related table where column = value.
    /// Used by the card drill-in to show connected data inline.
    /// </summary>
    [HttpGet]
    [Route("Database/RelatedRows/{db}/{table}/{column}/{value}")]
    public async Task<IActionResult> RelatedRows(string db, string table, string column, string value, int limit = 50)
    {
        if (!IsValidDatabase(db) || !IsValidTable(db, table))
            return BadRequest(new { error = "Invalid database or table" });
        if (!IsValidColumn(db, table, column))
            return BadRequest(new { error = $"Invalid column: {column}" });

        if (limit < 1) limit = 50;
        if (limit > 200) limit = 200;

        try
        {
            using var conn = GetConnection(db);
            var sql = $"SELECT * FROM `{table}` WHERE `{column}` = @val LIMIT {limit}";
            var rows = (await conn.QueryAsync(sql, new { val = value })).AsList();

            // Get column names from schema for the response
            var key = $"{db}.{table}";
            var schemaColumns = new List<string>();
            if (_schemaMap!.TryGetValue(key, out var schemaEl))
            {
                foreach (var col in schemaEl.GetProperty("columns").EnumerateArray())
                    schemaColumns.Add(col.GetProperty("name").GetString()!);
            }

            return Json(new
            {
                database = db,
                table,
                filterColumn = column,
                filterValue = value,
                columns = schemaColumns,
                totalRows = rows.Count,
                rows
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RelatedRows query failed: {Db}.{Table}.{Col}={Val}", db, table, column, value);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /Database/TableEdges/{db}/{table}
    /// Returns all relationship edges for a table (no specific row value needed),
    /// plus abbreviated schema for each connected table (for ER diagram rendering).
    /// </summary>
    [HttpGet]
    [Route("Database/TableEdges/{db}/{table}")]
    public IActionResult TableEdges(string db, string table)
    {
        if (!IsValidDatabase(db) || !IsValidTable(db, table))
            return BadRequest(new { error = "Invalid database or table" });

        if (_edges == null || _schemaMap == null)
            return StatusCode(500, new { error = "Relationship data not loaded" });

        var fullTable = $"{db}.{table}";

        // Find all edges where this table participates
        var edges = new List<object>();
        var connectedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in _edges)
        {
            var from = edge.GetProperty("from").GetString()!;
            var to = edge.GetProperty("to").GetString()!;
            var fromTable = string.Join(".", from.Split('.').Take(2));
            var toTable = string.Join(".", to.Split('.').Take(2));

            if (string.Equals(fromTable, fullTable, StringComparison.OrdinalIgnoreCase)
                || string.Equals(toTable, fullTable, StringComparison.OrdinalIgnoreCase))
            {
                var fromCol = from.Split('.').Last();
                var toCol = to.Split('.').Last();
                var score = edge.GetProperty("score").GetDouble();
                var confidence = edge.GetProperty("confidence").GetString()!;

                edges.Add(new
                {
                    fromTable,
                    fromCol,
                    toTable,
                    toCol,
                    score,
                    confidence,
                    direction = string.Equals(fromTable, fullTable, StringComparison.OrdinalIgnoreCase)
                        ? "outbound" : "inbound"
                });

                connectedTables.Add(fromTable);
                connectedTables.Add(toTable);
            }
        }

        // Build abbreviated schema for each connected table (name, columns with key/type, est_rows)
        var tableSchemas = new Dictionary<string, object>();
        foreach (var ct in connectedTables)
        {
            if (!_schemaMap.TryGetValue(ct, out var sch)) continue;
            if (!_tablesMap!.TryGetValue(ct, out var tbl)) continue;

            var cols = new List<object>();
            foreach (var col in sch.GetProperty("columns").EnumerateArray())
            {
                cols.Add(new
                {
                    name = col.GetProperty("name").GetString(),
                    type = col.GetProperty("type").GetString(),
                    key = col.GetProperty("key").GetString()
                });
            }

            tableSchemas[ct] = new
            {
                database = tbl.GetProperty("database").GetString(),
                table = tbl.GetProperty("table").GetString(),
                estRows = tbl.GetProperty("est_rows").GetInt64(),
                totalEdges = tbl.GetProperty("total_edges").GetInt32(),
                columns = cols
            };
        }

        return Json(new
        {
            centerTable = fullTable,
            edges,
            schemas = tableSchemas
        });
    }

    /// <summary>
    /// GET /Database/Export/{db}/{table}?filterCol=&filterVal=
    /// Streams the full table (or filtered subset) as a properly escaped CSV file.
    /// All fields are quoted to handle commas, newlines, and special characters safely.
    /// </summary>
    [HttpGet]
    [Route("Database/Export/{db}/{table}")]
    public async Task<IActionResult> Export(string db, string table, string? filterCol = null, string? filterVal = null)
    {
        if (!IsValidDatabase(db) || !IsValidTable(db, table))
            return BadRequest("Invalid database or table");

        var key = $"{db}.{table}";
        if (!_schemaMap!.TryGetValue(key, out var schemaEl))
            return BadRequest("Schema not found");

        // Get column names in order
        var columns = new List<string>();
        foreach (var col in schemaEl.GetProperty("columns").EnumerateArray())
            columns.Add(col.GetProperty("name").GetString()!);

        // Build query
        var whereClause = "";
        var parameters = new DynamicParameters();

        bool hasSingleFilter = !string.IsNullOrEmpty(filterCol) && !string.IsNullOrEmpty(filterVal)
                               && IsValidColumn(db, table, filterCol);
        bool hasGlobalSearch = !hasSingleFilter && !string.IsNullOrEmpty(filterVal) && string.IsNullOrEmpty(filterCol);

        if (hasSingleFilter)
        {
            whereClause = $"WHERE `{filterCol}` = @filterVal";
            parameters.Add("filterVal", filterVal);
        }
        else if (hasGlobalSearch)
        {
            var likeClauses = columns.Select(c => $"`{c}` LIKE @searchVal").ToList();
            if (likeClauses.Count > 0)
            {
                whereClause = "WHERE (" + string.Join(" OR ", likeClauses) + ")";
                parameters.Add("searchVal", $"%{filterVal}%");
            }
        }

        try
        {
            using var conn = GetConnection(db);
            var sql = $"SELECT * FROM `{table}` {whereClause}";
            var rows = (await conn.QueryAsync(sql, parameters)).AsList();

            // Build CSV in memory with proper RFC 4180 escaping
            var sb = new System.Text.StringBuilder();

            // BOM for Excel UTF-8 detection
            sb.Append('\uFEFF');

            // Header row
            sb.AppendLine(string.Join(",", columns.Select(CsvEscape)));

            // Data rows
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object>)row;
                var values = columns.Select(c =>
                {
                    dict.TryGetValue(c, out var val);
                    if (val == null || val == DBNull.Value) return CsvEscape("");
                    return CsvEscape(Convert.ToString(val) ?? "");
                });
                sb.AppendLine(string.Join(",", values));
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"{db}_{table}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            return File(bytes, "text/csv; charset=utf-8", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed: {Db}.{Table}", db, table);
            return StatusCode(500, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// RFC 4180 CSV field escaping. Always quotes every field for safety.
    /// Handles embedded quotes, commas, newlines, tabs, semicolons, and leading =/+/@/- 
    /// (formula injection prevention for Excel).
    /// </summary>
    private static string CsvEscape(string value)
    {
        // Escape any embedded double quotes by doubling them
        var escaped = value.Replace("\"", "\"\"");

        // Prevent Excel formula injection: if the value starts with =, +, -, @, tab, or CR
        // prefix with a single quote inside the quoted field (Excel ignores it visually)
        if (escaped.Length > 0)
        {
            var firstChar = escaped[0];
            if (firstChar == '=' || firstChar == '+' || firstChar == '-' || firstChar == '@'
                || firstChar == '\t' || firstChar == '\r')
            {
                escaped = "'" + escaped;
            }
        }

        // Always double-quote every field
        return "\"" + escaped + "\"";
    }

    /// <summary>
    /// POST /Database/Update — inline cell edit.
    /// Body: { db, table, pkColumns: [...], pkValues: [...], column, value }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Update([FromBody] CellUpdateRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.Db) || string.IsNullOrEmpty(req.Table))
            return BadRequest(new { error = "Missing parameters" });

        if (READ_ONLY_DBS.Contains(req.Db))
            return BadRequest(new { error = $"The {req.Db} database is read-only" });

        if (!IsValidDatabase(req.Db) || !IsValidTable(req.Db, req.Table))
            return BadRequest(new { error = "Invalid database or table" });

        if (!IsValidColumn(req.Db, req.Table, req.Column))
            return BadRequest(new { error = $"Invalid column: {req.Column}" });

        if (req.PkColumns == null || req.PkValues == null || req.PkColumns.Length != req.PkValues.Length || req.PkColumns.Length == 0)
            return BadRequest(new { error = "Primary key columns/values required" });

        // Validate all PK columns exist
        foreach (var pkCol in req.PkColumns)
        {
            if (!IsValidColumn(req.Db, req.Table, pkCol))
                return BadRequest(new { error = $"Invalid PK column: {pkCol}" });
        }

        try
        {
            using var conn = GetConnection(req.Db);

            // Capture before state
            var whereClause = string.Join(" AND ", req.PkColumns.Select((c, i) => $"`{c}` = @pk{i}"));
            var parameters = new DynamicParameters();
            for (int i = 0; i < req.PkColumns.Length; i++)
                parameters.Add($"pk{i}", req.PkValues[i]);

            var beforeRow = await conn.QueryFirstOrDefaultAsync($"SELECT * FROM `{req.Table}` WHERE {whereClause}", parameters);
            var stateBefore = beforeRow != null ? JsonSerializer.Serialize((IDictionary<string, object>)beforeRow) : null;

            // Build UPDATE
            parameters.Add("newVal", req.Value);
            var sql = $"UPDATE `{req.Table}` SET `{req.Column}` = @newVal WHERE {whereClause}";
            var affected = await conn.ExecuteAsync(sql, parameters);

            // Capture after state
            var afterRow = await conn.QueryFirstOrDefaultAsync($"SELECT * FROM `{req.Table}` WHERE {whereClause}", parameters);
            var stateAfter = afterRow != null ? JsonSerializer.Serialize((IDictionary<string, object>)afterRow) : null;

            // Audit
            await _audit.LogAsync(new AuditEntry
            {
                Category = "database",
                Action = "cell_edit",
                TargetType = $"{req.Db}.{req.Table}",
                TargetName = $"{req.Table}.{req.Column}",
                StateBefore = stateBefore,
                StateAfter = stateAfter,
                IsReversible = true,
                Success = affected > 0,
                Notes = $"SET `{req.Column}` = {req.Value ?? "NULL"} WHERE {string.Join(", ", req.PkColumns.Zip(req.PkValues, (c, v) => $"{c}={v}"))}"
            });

            return Json(new { success = true, affected });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cell update failed: {Db}.{Table}.{Col}", req.Db, req.Table, req.Column);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Database/Insert — new row.
    /// Body: { db, table, values: { col1: val1, col2: val2, ... } }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Insert([FromBody] RowInsertRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.Db) || string.IsNullOrEmpty(req.Table))
            return BadRequest(new { error = "Missing parameters" });

        if (READ_ONLY_DBS.Contains(req.Db))
            return BadRequest(new { error = $"The {req.Db} database is read-only" });

        if (!IsValidDatabase(req.Db) || !IsValidTable(req.Db, req.Table))
            return BadRequest(new { error = "Invalid database or table" });

        if (req.Values == null || req.Values.Count == 0)
            return BadRequest(new { error = "No values provided" });

        // Validate all columns
        foreach (var col in req.Values.Keys)
        {
            if (!IsValidColumn(req.Db, req.Table, col))
                return BadRequest(new { error = $"Invalid column: {col}" });
        }

        try
        {
            using var conn = GetConnection(req.Db);

            var columns = req.Values.Keys.ToList();
            var colList = string.Join(", ", columns.Select(c => $"`{c}`"));
            var paramList = string.Join(", ", columns.Select(c => $"@{c}"));

            var parameters = new DynamicParameters();
            foreach (var kvp in req.Values)
                parameters.Add(kvp.Key, kvp.Value?.ToString());

            var sql = $"INSERT INTO `{req.Table}` ({colList}) VALUES ({paramList})";
            var affected = await conn.ExecuteAsync(sql, parameters);

            // Audit
            await _audit.LogAsync(new AuditEntry
            {
                Category = "database",
                Action = "row_insert",
                TargetType = $"{req.Db}.{req.Table}",
                TargetName = req.Table,
                StateAfter = JsonSerializer.Serialize(req.Values),
                IsReversible = true,
                Success = affected > 0,
                Notes = $"Inserted into {req.Db}.{req.Table} ({columns.Count} columns)"
            });

            return Json(new { success = true, affected });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Insert failed: {Db}.{Table}", req.Db, req.Table);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /Database/Delete — delete row.
    /// Body: { db, table, pkColumns: [...], pkValues: [...] }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete([FromBody] RowDeleteRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.Db) || string.IsNullOrEmpty(req.Table))
            return BadRequest(new { error = "Missing parameters" });

        if (READ_ONLY_DBS.Contains(req.Db))
            return BadRequest(new { error = $"The {req.Db} database is read-only" });

        if (!IsValidDatabase(req.Db) || !IsValidTable(req.Db, req.Table))
            return BadRequest(new { error = "Invalid database or table" });

        if (req.PkColumns == null || req.PkValues == null || req.PkColumns.Length != req.PkValues.Length || req.PkColumns.Length == 0)
            return BadRequest(new { error = "Primary key columns/values required" });

        foreach (var pkCol in req.PkColumns)
        {
            if (!IsValidColumn(req.Db, req.Table, pkCol))
                return BadRequest(new { error = $"Invalid PK column: {pkCol}" });
        }

        try
        {
            using var conn = GetConnection(req.Db);

            var whereClause = string.Join(" AND ", req.PkColumns.Select((c, i) => $"`{c}` = @pk{i}"));
            var parameters = new DynamicParameters();
            for (int i = 0; i < req.PkColumns.Length; i++)
                parameters.Add($"pk{i}", req.PkValues[i]);

            // Capture before state
            var beforeRow = await conn.QueryFirstOrDefaultAsync($"SELECT * FROM `{req.Table}` WHERE {whereClause}", parameters);
            var stateBefore = beforeRow != null ? JsonSerializer.Serialize((IDictionary<string, object>)beforeRow) : null;

            var sql = $"DELETE FROM `{req.Table}` WHERE {whereClause}";
            var affected = await conn.ExecuteAsync(sql, parameters);

            // Audit
            await _audit.LogAsync(new AuditEntry
            {
                Category = "database",
                Action = "row_delete",
                TargetType = $"{req.Db}.{req.Table}",
                TargetName = req.Table,
                StateBefore = stateBefore,
                IsReversible = true,
                Success = affected > 0,
                Notes = $"Deleted from {req.Db}.{req.Table} WHERE {string.Join(", ", req.PkColumns.Zip(req.PkValues, (c, v) => $"{c}={v}"))}"
            });

            return Json(new { success = true, affected });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete failed: {Db}.{Table}", req.Db, req.Table);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ===================== REQUEST DTOs =====================

    public class CellUpdateRequest
    {
        public string Db { get; set; } = "";
        public string Table { get; set; } = "";
        public string[] PkColumns { get; set; } = Array.Empty<string>();
        public string[] PkValues { get; set; } = Array.Empty<string>();
        public string Column { get; set; } = "";
        public string? Value { get; set; }
    }

    public class RowInsertRequest
    {
        public string Db { get; set; } = "";
        public string Table { get; set; } = "";
        public Dictionary<string, object?> Values { get; set; } = new();
    }

    public class RowDeleteRequest
    {
        public string Db { get; set; } = "";
        public string Table { get; set; } = "";
        public string[] PkColumns { get; set; } = Array.Empty<string>();
        public string[] PkValues { get; set; } = Array.Empty<string>();
    }
}