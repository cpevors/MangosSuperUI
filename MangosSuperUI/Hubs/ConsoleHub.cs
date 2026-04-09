using Microsoft.AspNetCore.SignalR;
using MangosSuperUI.Services;

namespace MangosSuperUI.Hubs;

public class ConsoleHub : Hub
{
    private readonly RaService _raService;
    private readonly AuditService _audit;
    private readonly ILogger<ConsoleHub> _logger;

    public ConsoleHub(RaService raService, AuditService audit, ILogger<ConsoleHub> logger)
    {
        _raService = raService;
        _audit = audit;
        _logger = logger;
    }

    public async Task SendCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            await Clients.Caller.SendAsync("ReceiveResponse", "(empty command)", false);
            return;
        }

        _logger.LogInformation("Console command from {ConnectionId}: {Command}", Context.ConnectionId, command);

        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _raService, command, operatorIp: ip, notes: "SignalR console");

        await Clients.Caller.SendAsync("ReceiveResponse", success ? response : $"Error: {response}", success);
    }

    public async Task TestConnection()
    {
        try
        {
            var response = await _raService.SendCommandAsync(".server info");
            await Clients.Caller.SendAsync("ConnectionStatus", true, response);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("ConnectionStatus", false, ex.Message);
        }
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Console client connected: {ConnectionId}", Context.ConnectionId);
        await Clients.Caller.SendAsync("ReceiveResponse", "Connected to MangosSuperUI Console. Type .help for available commands.", true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Console client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}