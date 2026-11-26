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
    // Guards _locks and the reference counts. A target's entry exists only while a command holds or waits for its lock.
    private readonly Lock _sync = new();

    private readonly Dictionary<string, Entry> _locks = new();

    /// <summary>
    /// Waits until no other command runs against <paramref name="target"/>. Dispose the result to let the next one run.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string target, CancellationToken cancellationToken)
    {
        Entry entry;

        lock (_sync)
        {
            if (!_locks.TryGetValue(target, out entry!))
            {
                entry = new Entry();
                _locks[target] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled while waiting: the lock was never taken, only the reference is given back.
            Unreference(target, entry);

            throw;
        }

        return new Releaser(this, target, entry);
    }

    private void Release(string target, Entry entry)
    {
        entry.Semaphore.Release();

        Unreference(target, entry);
    }

    private void Unreference(string target, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.References > 0)
            {
                return;
            }

            // Nobody holds or waits for the lock anymore, and new callers create a new entry under _sync.
            _locks.Remove(target);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Commands holding or waiting for the lock. Only changed under <see cref="_sync"/>.</summary>
        public int References { get; set; }
    }

    private sealed class Releaser(RconTargetLocks owner, string target, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(target, entry);
            }
        }
    }
}
