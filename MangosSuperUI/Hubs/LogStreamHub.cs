using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Hubs;

public class LogStreamHub : Hub
{
    private static readonly Dictionary<string, LogTailer> _tailers = new();
    private static readonly object _lock = new();
    private readonly string _logDir;
    private readonly ILogger<LogStreamHub> _logger;

    /// <summary>
    /// Available log files and their display names.
    /// </summary>
    public static readonly Dictionary<string, string> LogFiles = new()
    {
        ["Server"] = "Server.log",
        ["Chat"] = "Chat.log",
        ["RA"] = "Ra.log",
        ["Network"] = "Network.log",
        ["Performance"] = "Perf.log",
        ["DBErrors"] = "DBErrors.log",
        ["Anticheat"] = "Anticheat.log",
        ["Character"] = "Char.log",
        ["Loot"] = "Loot.log",
        ["Trades"] = "Trades.log",
        ["LevelUp"] = "LevelUp.log",
        ["Battleground"] = "Bg.log",
        ["Scripts"] = "Scripts.log",
        ["Movement"] = "Movement.log",
        ["GMCritical"] = "gm_critical.log",
        ["Realmd"] = "Realmd.log"
    };

    public LogStreamHub(IConfiguration config, ILogger<LogStreamHub> logger)
    {
        _logger = logger;
        // Try MangosdLogsDir from config, fallback to known path
        _logDir = config.GetValue<string>("Vmangos:LogsDir") ?? "";
        if (string.IsNullOrEmpty(_logDir) || !Directory.Exists(_logDir))
            _logDir = "/home/wowvmangos/vmangos/run/bin";
    }

    /// <summary>
    /// Client subscribes to a log file. Sends last N lines immediately, then streams new lines.
    /// </summary>
    public async Task Subscribe(string logName, int tailLines = 100)
    {
        if (!LogFiles.ContainsKey(logName))
        {
            await Clients.Caller.SendAsync("LogError", $"Unknown log: {logName}");
            return;
        }

        var fileName = LogFiles[logName];
        var filePath = Path.Combine(_logDir, fileName);

        if (!File.Exists(filePath))
        {
            await Clients.Caller.SendAsync("LogError", $"Log file not found: {fileName}");
            return;
        }

        // Add this connection to the group for this log file
        await Groups.AddToGroupAsync(Context.ConnectionId, logName);

        // Send initial tail
        try
        {
            var lines = TailFile(filePath, tailLines);
            await Clients.Caller.SendAsync("LogInitial", logName, lines);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("LogError", $"Failed to read {fileName}: {ex.Message}");
            return;
        }

        // Ensure a tailer is running for this file
        EnsureTailer(logName, filePath);

        _logger.LogInformation("Client {ConnectionId} subscribed to {LogName}", Context.ConnectionId, logName);
    }

    /// <summary>
    /// Client unsubscribes from a log file.
    /// </summary>
    public async Task Unsubscribe(string logName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, logName);
        _logger.LogInformation("Client {ConnectionId} unsubscribed from {LogName}", Context.ConnectionId, logName);
    }

    /// <summary>
    /// Get available log files with sizes.
    /// </summary>
    public async Task GetAvailableLogs()
    {
        var logs = new List<object>();
        foreach (var kvp in LogFiles)
        {
            var filePath = Path.Combine(_logDir, kvp.Value);
            var exists = File.Exists(filePath);
            long size = 0;
            DateTime lastModified = default;

            if (exists)
            {
                var fi = new FileInfo(filePath);
                size = fi.Length;
                lastModified = fi.LastWriteTime;
            }

            logs.Add(new
            {
                name = kvp.Key,
                fileName = kvp.Value,
                exists,
                size,
                sizeFormatted = FormatSize(size),
                lastModified = lastModified > DateTime.MinValue ? lastModified.ToString("yyyy-MM-dd HH:mm:ss") : "—"
            });
        }

        await Clients.Caller.SendAsync("AvailableLogs", logs);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Client leaves all groups automatically
        _logger.LogInformation("LogStream client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ==================== Tailing ====================

    private void EnsureTailer(string logName, string filePath)
    {
        lock (_lock)
        {
            if (_tailers.ContainsKey(logName) && _tailers[logName].IsRunning)
                return;

            var hubContext = Context.GetHttpContext()!.RequestServices
                .GetRequiredService<IHubContext<LogStreamHub>>();

            var tailer = new LogTailer(logName, filePath, hubContext, _logger);
            _tailers[logName] = tailer;
            tailer.Start();
        }
    }

    private static List<string> TailFile(string path, int lines)
    {
        var result = new List<string>();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return result;

        // Read from the end
        var buffer = new byte[Math.Min(fs.Length, lines * 200)]; // estimate ~200 bytes per line
        var startPos = Math.Max(0, fs.Length - buffer.Length);
        fs.Seek(startPos, SeekOrigin.Begin);
        var bytesRead = fs.Read(buffer, 0, buffer.Length);

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
        var allLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Take last N lines
        var start = Math.Max(0, allLines.Length - lines);
        for (var i = start; i < allLines.Length; i++)
        {
            var line = allLines[i].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
                result.Add(line);
        }

        return result;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1048576.0:F1} MB";
    }
}

/// <summary>
/// Background file watcher that detects new lines and broadcasts via SignalR.
/// </summary>
public class LogTailer
{
    private readonly string _logName;
    private readonly string _filePath;
    private readonly IHubContext<LogStreamHub> _hubContext;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsRunning => _task != null && !_task.IsCompleted;

    public LogTailer(string logName, string filePath, IHubContext<LogStreamHub> hubContext, ILogger logger)
    {
        _logName = logName;
        _filePath = filePath;
        _hubContext = hubContext;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => TailLoop(_cts.Token));
    }

    private async Task TailLoop(CancellationToken ct)
    {
        try
        {
            // Start at the end of the file
            long lastPosition = 0;
            if (File.Exists(_filePath))
            {
                var fi = new FileInfo(_filePath);
                lastPosition = fi.Length;
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct); // Poll every 500ms

                if (!File.Exists(_filePath)) continue;

                var fi = new FileInfo(_filePath);

                // File was truncated/rotated — reset position
                if (fi.Length < lastPosition)
                {
                    lastPosition = 0;
                    await _hubContext.Clients.Group(_logName)
                        .SendAsync("LogLine", _logName, "[Log file was rotated/truncated]", ct);
                }

                if (fi.Length <= lastPosition) continue;

                // Read new content
                try
                {
                    using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(lastPosition, SeekOrigin.Begin);

                    var newBytes = new byte[fi.Length - lastPosition];
                    var read = await fs.ReadAsync(newBytes, ct);

                    if (read > 0)
                    {
                        lastPosition += read;
                        var text = System.Text.Encoding.UTF8.GetString(newBytes, 0, read);
                        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                        foreach (var line in lines)
                        {
                            var trimmed = line.TrimEnd('\r');
                            if (!string.IsNullOrWhiteSpace(trimmed))
                            {
                                await _hubContext.Clients.Group(_logName)
                                    .SendAsync("LogLine", _logName, trimmed, ct);
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // File might be locked momentarily — skip this cycle
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LogTailer error for {LogName}", _logName);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
    }
}
