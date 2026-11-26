namespace Kobblestone.Operator.Constants;

public static class JobDefaults
{
    /// <summary>
    /// Backup, restore and cleanup Jobs are deleted (with their Pods) this long after they finished. Finished Pods
    /// still count as users of the volumes they mounted, so a server's or repo's volume could not be deleted while
    /// they exist. The time leaves room to read the logs.
    /// </summary>
    public const int TtlSecondsAfterFinished = 600;
}
