using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// Labels on the `Pod`s Kobblestone creates, naming the `Server`s and `BackupRepo`s whose volumes they mount, and the
/// lookups built on them. They let `FileBrowser`s find the other `Pod`s using their volumes (and where they run) with
/// label selectors, instead of listing all `Pod`s.
/// </summary>
/// <remarks>
/// Only `Pod`s created by Kobblestone are labelled; `Pod`s created by users that mount the same volumes are not found.
/// </remarks>
public static class VolumeUsers
{
    /// <summary>Label of server `Pod`s (value: the server's name), set by the ServerController.</summary>
    public const string ServerLabel = "kobblestone.io/server";

    /// <summary>
    /// Label of backup and restore `Job` `Pod`s (value: the server whose volume they mount). The same label as on the
    /// `Job`s.
    /// </summary>
    public const string TargetServerLabel = ServerOperations.TargetServerLabel;

    /// <summary>Label of backup, restore and cleanup `Job` `Pod`s (value: the `BackupRepo` they use).</summary>
    public const string BackupRepoLabel = "kobblestone.io/backup-repo";

    /// <summary>
    /// Prefixes of the labels of file manager `Pod`s, one per mounted server or repo volume
    /// (<c>servers.kobblestone.io/&lt;server&gt;</c>, empty value), since a `FileBrowser` can mount several.
    /// </summary>
    public const string FileBrowserServerPrefix = "servers.kobblestone.io/";

    public const string FileBrowserRepoPrefix = "repos.kobblestone.io/";

    /// <summary>
    /// The `Pod`s using a server's volume: the server itself, its backup and restore `Job`s, and file managers.
    /// </summary>
    public static async Task<IReadOnlyList<V1Pod>> OfServerAsync(IKubernetesClient client, string @namespace,
        string server, CancellationToken cancellationToken) =>
        await ListAsync(client, @namespace, cancellationToken,
            new EqualsLabelSelector(ServerLabel, server),
            new EqualsLabelSelector(TargetServerLabel, server),
            new ExistsLabelSelector(FileBrowserServerPrefix + server)).ConfigureAwait(false);

    /// <summary>
    /// The `Pod`s using a repo's volume: backup, restore and cleanup `Job`s, and file managers.
    /// </summary>
    public static async Task<IReadOnlyList<V1Pod>> OfRepoAsync(IKubernetesClient client, string @namespace,
        string repo, CancellationToken cancellationToken) =>
        await ListAsync(client, @namespace, cancellationToken,
            new EqualsLabelSelector(BackupRepoLabel, repo),
            new ExistsLabelSelector(FileBrowserRepoPrefix + repo)).ConfigureAwait(false);

    /// <summary>
    /// The servers and repos whose volumes a `Pod` mounts, according to its labels.
    /// </summary>
    public static (IReadOnlyList<string> Servers, IReadOnlyList<string> Repos) Of(V1Pod pod)
    {
        var labels = pod.Metadata.Labels ?? new Dictionary<string, string>();

        var servers = labels
            .Where(l => l.Key is ServerLabel or TargetServerLabel)
            .Select(l => l.Value)
            .Concat(labels.Keys.Where(k => k.StartsWith(FileBrowserServerPrefix, StringComparison.Ordinal))
                .Select(k => k[FileBrowserServerPrefix.Length..]))
            .Distinct()
            .ToList();

        var repos = labels
            .Where(l => l.Key is BackupRepoLabel)
            .Select(l => l.Value)
            .Concat(labels.Keys.Where(k => k.StartsWith(FileBrowserRepoPrefix, StringComparison.Ordinal))
                .Select(k => k[FileBrowserRepoPrefix.Length..]))
            .Distinct()
            .ToList();

        return (servers, repos);
    }

    /// <summary>
    /// Pods matching any of the selectors (one list each: label selectors cannot express "or"), without duplicates.
    /// </summary>
    private static async Task<IReadOnlyList<V1Pod>> ListAsync(IKubernetesClient client, string @namespace,
        CancellationToken cancellationToken, params LabelSelector[] selectors)
    {
        var pods = new Dictionary<string, V1Pod>();

        foreach (var selector in selectors)
        {
            foreach (var pod in await client.ListAsync<V1Pod>(@namespace, labelSelector: selector,
                         cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                pods.TryAdd(pod.Name(), pod);
            }
        }

        return pods.Values.ToList();
    }
}
