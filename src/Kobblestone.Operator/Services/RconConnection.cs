using CoreRCON;
using CoreRCON.Parsers;

namespace Kobblestone.Operator.Services;

/// <remarks>
/// CoreRCON does not take cancellation tokens. The calls stop waiting when the token is cancelled; the connection is
/// closed when it is disposed.
/// </remarks>
public class RconConnection(RCON rcon) : IRconConnection
{
    public ValueTask DisposeAsync()
    {
        rcon.Dispose();

        return default;
    }

    public Task<string> SendCommandAsync(string command, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => rcon.SendCommandAsync(command, timeout).WaitAsync(cancellationToken);

    public Task<T> SendCommandAsync<T>(string command, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where T : class, IParseable, new() =>
        rcon.SendCommandAsync<T>(command, timeout).WaitAsync(cancellationToken);
}
