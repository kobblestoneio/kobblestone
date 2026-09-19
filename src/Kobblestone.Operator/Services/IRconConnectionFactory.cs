namespace Kobblestone.Operator.Services;

public interface IRconConnectionFactory
{
    Task<IRconConnection> ConnectAsync(
        string host,
        int port,
        string password,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}