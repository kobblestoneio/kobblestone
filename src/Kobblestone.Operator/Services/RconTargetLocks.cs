using System.Collections.Concurrent;

namespace Kobblestone.Operator.Services;

/// <summary>
/// Runs RCON commands one at a time per target. Vanilla Minecraft (and the servers based on its code, e.g. Fabric)
/// collects the output of every RCON command in one buffer shared by all RCON connections: it clears the buffer, runs
/// the command and returns the buffer. Commands sent at the same time over different connections get each other's
/// output. Paper and its forks give every connection its own buffer, but serializing is cheap, so it is done for all.
/// </summary>
/// <remarks>
/// Only the elected leader reconciles, so a lock in the operator process is enough. Commands sent by others, e.g. the
/// server image's own RCON commands (`autoCommands`), are not covered.
/// </remarks>
public sealed class RconTargetLocks
{
    // One semaphore per target that ever received a command; they are small, so they are kept.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Waits until no other command runs against <paramref name="target"/>. Dispose the result to let the next one run.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string target, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
