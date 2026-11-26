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

        try
        {
            // CoreRCON does not take cancellation tokens: stop waiting when the token is cancelled.
            await rcon.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            rcon.Dispose();
            throw;
        }

        return new RconConnection(rcon);
    }
}
