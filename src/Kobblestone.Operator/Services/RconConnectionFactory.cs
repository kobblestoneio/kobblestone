using System.Net;

using CoreRCON;

using Microsoft.Extensions.Logging;

namespace Kobblestone.Operator.Services;

public class RconConnectionFactory(ILoggerFactory loggerFactory) : IRconConnectionFactory
{
    public async Task<IRconConnection> ConnectAsync(
        string host,
        int port,
        string password,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var rcon = new RCON(IPAddress.Parse(host), (ushort)port, password, autoConnect: false,
            timeout: ((uint?)timeout?.TotalMilliseconds) ?? 0,
            logger: loggerFactory.CreateLogger("Rcon"));

        await rcon.ConnectAsync().ConfigureAwait(false);

        return new RconConnection(rcon);
    }
}