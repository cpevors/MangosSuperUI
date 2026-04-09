using MySqlConnector;

namespace MangosSuperUI.Models;

public class ConnectionFactory
{
    private readonly IConfiguration _config;

    public ConnectionFactory(IConfiguration config)
    {
        _config = config;
    }

    public MySqlConnection Mangos() => new(_config.GetConnectionString("Mangos"));
    public MySqlConnection Characters() => new(_config.GetConnectionString("Characters"));
    public MySqlConnection Realmd() => new(_config.GetConnectionString("Realmd"));
    public MySqlConnection Logs() => new(_config.GetConnectionString("Logs"));
    public MySqlConnection Admin() => new(_config.GetConnectionString("Admin"));
}
