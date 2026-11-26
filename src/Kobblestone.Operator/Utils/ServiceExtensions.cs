using k8s.Models;

namespace Kobblestone.Operator.Utils;

public static class ServiceExtensions
{
    /// <summary>
    /// Domain of the cluster's DNS. Service DNS names are <c>&lt;name&gt;.&lt;namespace&gt;.svc.&lt;domain&gt;</c>.
    /// </summary>
    public const string ClusterDomain = "cluster.local";

    /// <summary>
    /// Stable DNS name of a Service inside the cluster, e.g. <c>my-server.minecraft.svc.cluster.local</c>.
    /// </summary>
    public static string ServiceDnsName(string name, string ns) => $"{name}.{ns}.svc.{ClusterDomain}";

    /// <summary>
    /// Returns the address under which a Service is reachable, preferring external addresses:
    /// <list type="number">
    /// <item>the first IP assigned by a load balancer,</item>
    /// <item>the first hostname assigned by a load balancer (e.g. AWS ELBs only provide a hostname),</item>
    /// <item>the first manually configured external IP,</item>
    /// <item>the Service's DNS name inside the cluster (never its cluster IP, which changes when the Service is
    /// recreated).</item>
    /// </list>
    /// </summary>
    public static string GetAddress(this V1Service service)
    {
        var ingress = service.Status?.LoadBalancer?.Ingress ?? [];

        return ingress.Select(i => i.Ip).FirstOrDefault(ip => !string.IsNullOrEmpty(ip))
               ?? ingress.Select(i => i.Hostname).FirstOrDefault(host => !string.IsNullOrEmpty(host))
               ?? service.Spec?.ExternalIPs?.FirstOrDefault(ip => !string.IsNullOrEmpty(ip))
               ?? ServiceDnsName(service.Metadata.Name, service.Metadata.NamespaceProperty);
    }
}
