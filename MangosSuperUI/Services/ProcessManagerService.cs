using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Services;

public class ProcessManagerService
{
    private readonly VmangosSettings _settings;
    private readonly ILogger<ProcessManagerService> _logger;

    public ProcessManagerService(IOptions<VmangosSettings> settings, ILogger<ProcessManagerService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public ProcessStatus GetMangosdStatus() => GetProcessStatus(_settings.MangosdProcess);
    public ProcessStatus GetRealmdStatus() => GetProcessStatus(_settings.RealmdProcess);

    public async Task<string> StartMangosdAsync() => await RunSystemctlAsync("start", "mangosd");
    public async Task<string> StopMangosdAsync() => await RunSystemctlAsync("stop", "mangosd");
    public async Task<string> RestartMangosdAsync() => await RunSystemctlAsync("restart", "mangosd");

    public async Task<string> StartRealmdAsync() => await RunSystemctlAsync("start", "realmd");
    public async Task<string> StopRealmdAsync() => await RunSystemctlAsync("stop", "realmd");
    public async Task<string> RestartRealmdAsync() => await RunSystemctlAsync("restart", "realmd");

    private async Task<string> RunSystemctlAsync(string action, string unit)
    {
        _logger.LogInformation("Running systemctl {Action} {Unit}", action, unit);

        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"systemctl {action} {unit}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start systemctl {action} {unit}");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            _logger.LogError("systemctl {Action} {Unit} failed (exit {Code}): {Error}", action, unit, proc.ExitCode, stderr);
            throw new InvalidOperationException($"systemctl {action} {unit} failed: {stderr.Trim()}");
        }

        _logger.LogInformation("systemctl {Action} {Unit} succeeded", action, unit);
        return stdout.Trim();
    }

    private ProcessStatus GetProcessStatus(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length > 0)
            {
                var proc = processes[0];
                return new ProcessStatus
                {
                    IsRunning = true,
                    Pid = proc.Id,
                    StartTime = proc.StartTime,
                    Uptime = DateTime.Now - proc.StartTime
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check process status for {Process}", processName);
        }

        return new ProcessStatus { IsRunning = false };
    }
}

public class ProcessStatus
{
    public bool IsRunning { get; set; }
    public int? Pid { get; set; }
    public DateTime? StartTime { get; set; }
    public TimeSpan? Uptime { get; set; }
}