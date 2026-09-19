using System.Net;

using k8s;

namespace Kobblestone.Operator.Services;

public class RouterApi(IHttpClientFactory httpClientFactory) : IRouterApi
{
    public async Task<Dictionary<string, RouterRoute>> GetRoutesAsync(string baseUrl,
        CancellationToken cancellationToken = default)
    {
        // If we are not in cluster, we cannot talk directly to the router pods, so we short-circuit here.
        if (!KubernetesClientConfiguration.IsInCluster())
        {
            return [];
        }

        using var client = httpClientFactory.CreateClient();

        var routes = await client
            .GetFromJsonAsync<Dictionary<string, RouterRoute>>($"{baseUrl}/routes", cancellationToken)
            .ConfigureAwait(false);

        return routes ?? [];
    }

    public async Task RegisterRouteAsync(string baseUrl, string hostname, string backend,
        CancellationToken cancellationToken = default)
    {
        // If we are not in cluster, we cannot talk directly to the router pods, so we short-circuit here.
        if (!KubernetesClientConfiguration.IsInCluster())
        {
            return;
        }

        using var client = httpClientFactory.CreateClient();

        await client.PostAsJsonAsync($"{baseUrl}/routes", new { serverAddress = hostname, backend }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteRouteAsync(string baseUrl, string hostname, CancellationToken cancellationToken = default)
    {
        // If we are not in cluster, we cannot talk directly to the router pods, so we short-circuit here.
        if (!KubernetesClientConfiguration.IsInCluster())
        {
            return;
        }

        using var client = httpClientFactory.CreateClient();

        try
        {
            await client.DeleteAsync($"{baseUrl}/routes/{hostname}", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex is { StatusCode: HttpStatusCode.NotFound })
        {
        }
    }
}