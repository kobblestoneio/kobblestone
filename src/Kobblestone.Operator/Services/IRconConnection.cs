using CoreRCON.Parsers;

namespace Kobblestone.Operator.Services;

public interface IRconConnection : IAsyncDisposable
{
    Task<string> SendCommandAsync(string command, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<T> SendCommandAsync<T>(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        where T : class, IParseable, new();
}