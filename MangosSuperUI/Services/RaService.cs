using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Services;

public class RaService : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly RemoteAccessSettings _settings;
    private readonly ILogger<RaService> _logger;
    private bool _authenticated;

    public bool IsConnected => _client?.Connected == true && _authenticated;

    public RaService(IOptions<RemoteAccessSettings> settings, ILogger<RaService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);

            var cmdBytes = Encoding.UTF8.GetBytes(command + "\n");
            await _stream!.WriteAsync(cmdBytes, ct);
            await _stream.FlushAsync(ct);

            var raw = await ReadResponseAsync(ct);
            return CleanResponse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RA command failed: {Command}", command);
            Disconnect();
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected)
            return;

        Disconnect();

        _logger.LogInformation("Connecting to RA at {Host}:{Port}", _settings.Host, _settings.Port);

        _client = new TcpClient();
        await _client.ConnectAsync(_settings.Host, _settings.Port, ct);
        _stream = _client.GetStream();

        // Read the welcome banner ("Welcome to World of Warcraft!")
        await ReadResponseAsync(ct);

        // Send username
        var userBytes = Encoding.UTF8.GetBytes(_settings.Username + "\n");
        await _stream.WriteAsync(userBytes, ct);
        await _stream.FlushAsync(ct);

        // Read auth response ("Patch 1.12: Drums of War is now live!")
        var authResponse = await ReadResponseAsync(ct);
        _logger.LogInformation("RA auth response: {Response}", authResponse);

        // Send password
        var passBytes = Encoding.UTF8.GetBytes(_settings.Password + "\n");
        await _stream.WriteAsync(passBytes, ct);
        await _stream.FlushAsync(ct);

        // Read post-auth response (if any)
        await ReadResponseAsync(ct);

        _authenticated = true;
        _logger.LogInformation("RA connection established and authenticated");
    }

    /// <summary>
    /// Reads from the stream until no more data arrives within the idle timeout.
    /// VMaNGOS RA has no prompt � we detect end-of-response by silence.
    /// </summary>
    private async Task<string> ReadResponseAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        var idleTimeoutMs = 800;
        var overallTimeoutMs = _settings.CommandTimeoutMs;

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(overallTimeoutMs);

        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            idleCts.CancelAfter(idleTimeoutMs);

            try
            {
                var bytesRead = await _stream!.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token);
                if (bytesRead == 0)
                    throw new IOException("RA connection closed by remote host");

                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }
            catch (OperationCanceledException) when (!overallCts.Token.IsCancellationRequested)
            {
                // Idle timeout � no more data coming, response is complete
                break;
            }
            catch (OperationCanceledException)
            {
                // Overall timeout
                if (sb.Length > 0)
                    break;

                throw new TimeoutException($"RA response timed out after {overallTimeoutMs}ms");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Strips the +mangos> / -mangos> prompt and trailing whitespace from RA responses.
    /// </summary>
    private static string CleanResponse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var cleaned = raw;
        if (cleaned.EndsWith("+mangos>"))
            cleaned = cleaned[..^"+mangos>".Length];
        else if (cleaned.EndsWith("-mangos>"))
            cleaned = cleaned[..^"-mangos>".Length];
        else if (cleaned.EndsWith("mangos>"))
            cleaned = cleaned[..^"mangos>".Length];

        return cleaned.TrimEnd();
    }

    private void Disconnect()
    {
        _authenticated = false;
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
        _semaphore.Dispose();
    }
}