using CoreRCON;
using CoreRCON.Parsers;

namespace Kobblestone.Operator.Services;

public class RconConnection(RCON rcon) : IRconConnection
{
    public ValueTask DisposeAsync()
    {
        rcon.Dispose();

        return default;
    }

    public Task<string> SendCommandAsync(string command, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => rcon.SendCommandAsync(command, timeout);

    public Task<T> SendCommandAsync<T>(string command, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where T : class, IParseable, new() => rcon.SendCommandAsync<T>(command, timeout);
}