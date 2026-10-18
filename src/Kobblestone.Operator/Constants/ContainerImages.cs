namespace Kobblestone.Operator.Constants;

public static class ContainerImages
{
    public static string MinecraftServer(string? javaVersion)
        => $"itzg/minecraft-server:2026.10.0-{(javaVersion ?? "java25")}";

    public const string Router = "itzg/mc-router:1.48.0";

    public const string Proxy = "itzg/mc-proxy:java25-2026.10.0";

    public const string Backup = "itzg/mc-backup:2026.10.0";

    public const string AccountAuth = "ghcr.io/kobblestoneio/account-auth:0.2.0";

    public const string BotRunner = "ghcr.io/kobblestoneio/bot-runner:0.2.0";

    /// <summary>
    /// Used by backup, restore and cleanup Jobs for plain file operations (sh, ls, tar, mv, rm).
    /// </summary>
    public const string Ubuntu = "ubuntu:26.04";

    /// <summary>
    /// Used by backup, restore and cleanup Jobs for S3 repositories. Since 2.23.0, the AWS CLI sends checksums that many
    /// S3-compatible providers reject, so Jobs must set <c>AWS_REQUEST_CHECKSUM_CALCULATION</c> and
    /// <c>AWS_RESPONSE_CHECKSUM_VALIDATION</c> to <c>when_required</c>.
    /// </summary>
    public const string AwsCli = "amazon/aws-cli:2.37.13";
}
