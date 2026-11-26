using k8s.Models;

using Kobblestone.Operator.Entities.V1Alpha1;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// In-memory index of the servers and repos whose volumes `FileBrowser`s mount, so the `FileBrowser`s of a server or a
/// repo can be found without listing all of them: `spec.targets` is a list, which the API server cannot filter by (no
/// selectable field).
/// </summary>
/// <remarks>
/// The FileBrowserController records a `FileBrowser` on every reconcile (its servers at the start, its repos once its
/// targets are resolved) and removes it when it is deleted. It reconciles every `FileBrowser` when the operator starts,
/// so the index is complete shortly after. Until then, and between a spec change and its reconcile, a lookup can miss a
/// `FileBrowser`; that is harmless, because the `FileBrowser`'s own reconcile places it anyway. Stale entries only queue
/// `FileBrowser`s that no longer use the server or repo, or no longer exist, which their reconcile (or the queue)
/// ignores.
/// </remarks>
public sealed class FileBrowserIndex
{
    private readonly Lock _lock = new();

    private readonly Map _servers = new();

    private readonly Map _repos = new();

    /// <summary>
    /// Records the `Server` targets of a `FileBrowser`, replacing what was recorded before.
    /// </summary>
    public void Set(V1Alpha1FileBrowser fileBrowser)
    {
        var servers = fileBrowser.Spec.Targets
            .Where(t => t.TargetRef.Kind is null or V1Alpha1FileBrowser.FileBrowserTargetKind.Server)
            .Select(t => t.TargetRef.Name);

        lock (_lock)
        {
            _servers.Set(fileBrowser.Namespace(), fileBrowser.Name(), servers);
        }
    }

    /// <summary>
    /// Records the `BackupRepo`s whose volumes a `FileBrowser` mounts, replacing what was recorded before.
    /// </summary>
    public void SetRepos(string @namespace, string fileBrowser, IEnumerable<string> repos)
    {
        lock (_lock)
        {
            _repos.Set(@namespace, fileBrowser, repos);
        }
    }

    public void Remove(string @namespace, string fileBrowser)
    {
        lock (_lock)
        {
            _servers.Set(@namespace, fileBrowser, []);
            _repos.Set(@namespace, fileBrowser, []);
        }
    }

    /// <summary>
    /// The `FileBrowser`s that target a server.
    /// </summary>
    public IReadOnlyList<string> FileBrowsersOf(string @namespace, string server)
    {
        lock (_lock)
        {
            return _servers.Get(@namespace, server);
        }
    }

    /// <summary>
    /// The `FileBrowser`s that mount the volume of a `BackupRepo`.
    /// </summary>
    public IReadOnlyList<string> FileBrowsersOfRepo(string @namespace, string repo)
    {
        lock (_lock)
        {
            return _repos.Get(@namespace, repo);
        }
    }

    /// <summary>
    /// A bidirectional map between `FileBrowser`s and the names they reference, per namespace.
    /// </summary>
    private sealed class Map
    {
        private readonly Dictionary<(string Namespace, string FileBrowser), HashSet<string>> _byFileBrowser = new();

        private readonly Dictionary<(string Namespace, string Name), HashSet<string>> _byName = new();

        public void Set(string @namespace, string fileBrowser, IEnumerable<string> names)
        {
            if (_byFileBrowser.Remove((@namespace, fileBrowser), out var previous))
            {
                foreach (var name in previous)
                {
                    if (_byName.TryGetValue((@namespace, name), out var fileBrowsers) &&
                        fileBrowsers.Remove(fileBrowser) && fileBrowsers.Count == 0)
                    {
                        _byName.Remove((@namespace, name));
                    }
                }
            }

            var current = names.ToHashSet();

            if (current.Count == 0)
            {
                return;
            }

            _byFileBrowser[(@namespace, fileBrowser)] = current;

            foreach (var name in current)
            {
                if (!_byName.TryGetValue((@namespace, name), out var fileBrowsers))
                {
                    _byName[(@namespace, name)] = fileBrowsers = [];
                }

                fileBrowsers.Add(fileBrowser);
            }
        }

        public IReadOnlyList<string> Get(string @namespace, string name) =>
            _byName.TryGetValue((@namespace, name), out var fileBrowsers) ? fileBrowsers.ToList() : [];
    }
}
