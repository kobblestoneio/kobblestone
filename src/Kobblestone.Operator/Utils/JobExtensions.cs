using k8s.Models;

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
}
