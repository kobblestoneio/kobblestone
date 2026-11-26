namespace Kobblestone.Operator.Constants;

public static class JobDefaults
{
    /// <summary>
    /// Default of `job.ttlSecondsAfterFailure` of `Backup`s and `Restore`s: backup, restore and cleanup Jobs are deleted
    /// (with their Pods) this long after they finished. The operator deletes successful Jobs right away once it
    /// recorded their result (except the cleanup Jobs of `retainPolicy: Delete`, whose result it does not wait for),
    /// so this leaves room to read the logs of failed ones. Finished Pods still count as users of the volumes they
    /// mounted, so a server's or repo's volume cannot be deleted while they exist.
    /// </summary>
    public const int TtlSecondsAfterFailure = 600;
}
