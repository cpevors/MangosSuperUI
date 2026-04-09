using MySqlConnector;
using Dapper;

namespace MangosSuperUI.Services;

/// <summary>
/// Runs at app startup: ensures vmangos_admin database and its tables exist.
/// Exposes per-database health status for the dashboard.
/// Singleton — registered in Program.cs, kicked off after app.Build().
/// </summary>
public class DbInitializationService
{
    private readonly IConfiguration _config;
    private readonly ILogger<DbInitializationService> _logger;

    // Tracks init result for dashboard display
    public bool AdminDbReady { get; private set; }
    public string? AdminDbError { get; private set; }
    public DateTime? InitializedAt { get; private set; }
    public int TablesCreated { get; private set; }
    public int TablesExisted { get; private set; }

    public DbInitializationService(IConfiguration config, ILogger<DbInitializationService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Called once at startup from Program.cs. Creates DB + tables if missing.
    /// Never throws — logs errors and sets AdminDbReady = false.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("DbInitializationService: Starting vmangos_admin bootstrap...");

        try
        {
            // Step 1: Parse the Admin connection string and strip the Database part
            var adminConnStr = _config.GetConnectionString("Admin");
            if (string.IsNullOrEmpty(adminConnStr))
            {
                AdminDbError = "No 'Admin' connection string configured in appsettings.json or server-config.json.";
                _logger.LogError(AdminDbError);
                return;
            }

            var builder = new MySqlConnectionStringBuilder(adminConnStr);
            var dbName = builder.Database; // "vmangos_admin"
            builder.Database = "";         // Connect without specifying a DB

            // Step 2: Create the database if it doesn't exist
            using (var bootstrapConn = new MySqlConnection(builder.ConnectionString))
            {
                await bootstrapConn.OpenAsync();
                _logger.LogInformation("DbInitializationService: Connected to MariaDB server. Ensuring database '{Db}' exists...", dbName);

                await bootstrapConn.ExecuteAsync(
                    $"CREATE DATABASE IF NOT EXISTS `{dbName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci");
            }

            // Step 3: Create tables if they don't exist
            using (var conn = new MySqlConnection(adminConnStr))
            {
                await conn.OpenAsync();

                var created = 0;
                var existed = 0;

                // --- audit_log ---
                if (await TableExistsAsync(conn, dbName, "audit_log"))
                {
                    existed++;
                    _logger.LogDebug("DbInitializationService: audit_log already exists");
                }
                else
                {
                    await conn.ExecuteAsync(Sql_AuditLog);
                    await conn.ExecuteAsync(Sql_AuditLogIndexes);
                    created++;
                    _logger.LogInformation("DbInitializationService: Created audit_log table with indexes");
                }

                // --- config_history ---
                if (await TableExistsAsync(conn, dbName, "config_history"))
                {
                    existed++;
                    _logger.LogDebug("DbInitializationService: config_history already exists");
                }
                else
                {
                    await conn.ExecuteAsync(Sql_ConfigHistory);
                    await conn.ExecuteAsync(Sql_ConfigHistoryIndexes);
                    created++;
                    _logger.LogInformation("DbInitializationService: Created config_history table with indexes");
                }

                // --- scheduled_actions ---
                if (await TableExistsAsync(conn, dbName, "scheduled_actions"))
                {
                    existed++;
                    _logger.LogDebug("DbInitializationService: scheduled_actions already exists");
                }
                else
                {
                    await conn.ExecuteAsync(Sql_ScheduledActions);
                    await conn.ExecuteAsync(Sql_ScheduledActionsIndexes);
                    created++;
                    _logger.LogInformation("DbInitializationService: Created scheduled_actions table with indexes");
                }

                // --- og_baseline_meta ---
                if (await TableExistsAsync(conn, dbName, "og_baseline_meta"))
                {
                    existed++;
                    _logger.LogDebug("DbInitializationService: og_baseline_meta already exists");
                }
                else
                {
                    await conn.ExecuteAsync(Sql_OgBaselineMeta);
                    created++;
                    _logger.LogInformation("DbInitializationService: Created og_baseline_meta table");
                }

                TablesCreated = created;
                TablesExisted = existed;
            }

            AdminDbReady = true;
            InitializedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "DbInitializationService: Bootstrap complete. Created={Created}, AlreadyExisted={Existed}",
                TablesCreated, TablesExisted);
        }
        catch (Exception ex)
        {
            AdminDbReady = false;
            AdminDbError = ex.Message;
            _logger.LogError(ex, "DbInitializationService: Failed to bootstrap vmangos_admin");
        }
    }

    /// <summary>
    /// Checks connectivity to each configured database. Called by HomeController.DbHealth().
    /// </summary>
    public async Task<DbHealthReport> CheckHealthAsync()
    {
        var report = new DbHealthReport
        {
            AdminInitialized = AdminDbReady,
            AdminInitError = AdminDbError,
            InitializedAt = InitializedAt,
            TablesCreated = TablesCreated,
            TablesExisted = TablesExisted
        };

        report.Databases["mangos"] = await PingDatabaseAsync("Mangos");
        report.Databases["characters"] = await PingDatabaseAsync("Characters");
        report.Databases["realmd"] = await PingDatabaseAsync("Realmd");
        report.Databases["logs"] = await PingDatabaseAsync("Logs");
        report.Databases["vmangos_admin"] = await PingDatabaseAsync("Admin");

        return report;
    }

    private async Task<DbPingResult> PingDatabaseAsync(string connStringName)
    {
        var result = new DbPingResult();
        var connStr = _config.GetConnectionString(connStringName);

        if (string.IsNullOrEmpty(connStr))
        {
            result.Reachable = false;
            result.Error = "Connection string not configured";
            return result;
        }

        try
        {
            using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();
            await conn.ExecuteScalarAsync<int>("SELECT 1");
            result.Reachable = true;
        }
        catch (Exception ex)
        {
            result.Reachable = false;
            result.Error = ex.Message;
        }

        return result;
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection conn, string database, string table)
    {
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table",
            new { database, table });
        return count > 0;
    }

    // ==================== DDL Statements ====================

    private const string Sql_AuditLog = @"
        CREATE TABLE audit_log (
            id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            timestamp       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            operator        VARCHAR(64)     NOT NULL DEFAULT 'system',
            operator_ip     VARCHAR(45)     NULL,
            category        VARCHAR(32)     NOT NULL,
            action          VARCHAR(64)     NOT NULL,
            target_type     VARCHAR(32)     NULL,
            target_name     VARCHAR(128)    NULL,
            target_id       INT UNSIGNED    NULL,
            ra_command      TEXT            NULL,
            ra_response     TEXT            NULL,
            state_before    JSON            NULL,
            state_after     JSON            NULL,
            is_reversible   TINYINT(1)      NOT NULL DEFAULT 0,
            reverses_id     BIGINT UNSIGNED NULL,
            success         TINYINT(1)      NOT NULL DEFAULT 1,
            notes           TEXT            NULL
        ) ENGINE=InnoDB;";

    private const string Sql_AuditLogIndexes = @"
        CREATE INDEX idx_timestamp   ON audit_log (timestamp);
        CREATE INDEX idx_category    ON audit_log (category);
        CREATE INDEX idx_action      ON audit_log (action);
        CREATE INDEX idx_target      ON audit_log (target_type, target_name);
        CREATE INDEX idx_operator    ON audit_log (operator);
        CREATE INDEX idx_reversible  ON audit_log (is_reversible);";

    private const string Sql_ConfigHistory = @"
        CREATE TABLE config_history (
            id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            timestamp       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            operator        VARCHAR(64)     NOT NULL DEFAULT 'system',
            config_json     MEDIUMTEXT      NOT NULL,
            changes         JSON            NULL,
            notes           TEXT            NULL
        ) ENGINE=InnoDB;";

    private const string Sql_ConfigHistoryIndexes = @"
        CREATE INDEX idx_timestamp ON config_history (timestamp);";

    private const string Sql_ScheduledActions = @"
        CREATE TABLE scheduled_actions (
            id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            created_at      DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            execute_at      DATETIME(3)     NOT NULL,
            executed_at     DATETIME(3)     NULL,
            operator        VARCHAR(64)     NOT NULL DEFAULT 'system',
            action_type     VARCHAR(64)     NOT NULL,
            action_data     JSON            NOT NULL,
            status          VARCHAR(16)     NOT NULL DEFAULT 'pending',
            result          TEXT            NULL,
            audit_log_id    BIGINT UNSIGNED NULL
        ) ENGINE=InnoDB;";

    private const string Sql_ScheduledActionsIndexes = @"
        CREATE INDEX idx_execute_at ON scheduled_actions (execute_at);
        CREATE INDEX idx_status     ON scheduled_actions (status);";

    private const string Sql_OgBaselineMeta = @"
        CREATE TABLE IF NOT EXISTS og_baseline_meta (
            id              INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
            table_name      VARCHAR(64)     NOT NULL,
            source_table    VARCHAR(64)     NOT NULL,
            source_database VARCHAR(64)     NOT NULL DEFAULT 'mangos',
            row_count       INT UNSIGNED    NOT NULL DEFAULT 0,
            created_at      DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
            UNIQUE KEY idx_table (table_name)
        ) ENGINE=InnoDB;";
}

// ==================== Health DTOs ====================

public class DbHealthReport
{
    public bool AdminInitialized { get; set; }
    public string? AdminInitError { get; set; }
    public DateTime? InitializedAt { get; set; }
    public int TablesCreated { get; set; }
    public int TablesExisted { get; set; }
    public Dictionary<string, DbPingResult> Databases { get; set; } = new();
}

public class DbPingResult
{
    public bool Reachable { get; set; }
    public string? Error { get; set; }
}
