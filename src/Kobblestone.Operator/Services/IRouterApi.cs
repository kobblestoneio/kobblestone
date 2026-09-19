namespace Kobblestone.Operator.Services;

public interface IRouterApi
{
    Task<Dictionary<string, RouterRoute>> GetRoutesAsync(string baseUrl, CancellationToken cancellationToken = default);

    Task RegisterRouteAsync(string baseUrl, string hostname, string backend,
        CancellationToken cancellationToken = default);

    Task DeleteRouteAsync(string baseUrl, string hostname, CancellationToken cancellationToken = default);
}