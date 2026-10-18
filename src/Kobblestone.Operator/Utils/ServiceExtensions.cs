using k8s.Models;

namespace Kobblestone.Operator.Utils;

public static class ServiceExtensions
{
    /// <summary>
    /// Returns the IP address under which a Service is reachable, preferring external addresses:
    /// <list type="number">
    /// <item>the first IP assigned by a load balancer (ingress entries that only have a hostname are skipped),</item>
    /// <item>the first manually configured external IP,</item>
    /// <item>the cluster IP (not for headless Services).</item>
    /// </list>
    /// Returns <c>null</c> if the Service has no IP address yet.
    /// </summary>
    public static string? GetIpAddress(this V1Service service)
        => service.Status?.LoadBalancer?.Ingress?.Select(i => i.Ip).FirstOrDefault(ip => !string.IsNullOrEmpty(ip))
           ?? service.Spec?.ExternalIPs?.FirstOrDefault(ip => !string.IsNullOrEmpty(ip))
           ?? (service.Spec?.ClusterIP is { Length: > 0 } clusterIp and not "None" ? clusterIp : null);
}
