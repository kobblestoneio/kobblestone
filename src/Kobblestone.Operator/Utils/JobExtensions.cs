using System.Net;

using k8s;
using k8s.Autorest;
using k8s.Models;

using KubeOps.KubernetesClient;

namespace Kobblestone.Operator.Utils;

public static class JobExtensions
{
    extension(V1Job job)
    {
        /// <summary>Whether the `Job` completed successfully.</summary>
        public bool IsSucceeded()
            => job.Status?.Conditions?.Any(c => c.Type is "Complete" && c.Status is "True") is true;

        /// <summary>Whether the `Job` failed.</summary>
        public bool IsFailed()
            => job.Status?.Conditions?.Any(c => c.Type is "Failed" && c.Status is "True") is true;

        /// <summary>Whether the `Job` finished, successfully or not.</summary>
        public bool IsFinished() => job.IsSucceeded() || job.IsFailed();
    }

    /// <summary>
    /// Deletes a `Job` together with its `Pod`s. The API's default for `Job`s keeps (orphans) the `Pod`s, which would
    /// keep using their volumes.
    /// </summary>
    public static async Task DeleteJobWithPodsAsync(this IKubernetesClient client, V1Job job,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.ApiClient.BatchV1.DeleteNamespacedJobAsync(job.Name(), job.Namespace(),
                propagationPolicy: "Background", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode is HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }
}
