using Microsoft.Extensions.Options;

namespace Kobblestone.BotRunner;

/// <summary>
/// File system layout of a bot `Pod` and process settings. The defaults match the operator's `Pod` layout.
/// </summary>
/// <remarks>
/// Bound from environment variables with the prefix <see cref="EnvironmentPrefix"/> (e.g. <c>BOT_CONFIGDIRECTORY</c>)
/// and command line arguments (e.g. <c>--config-dir /etc/bot</c>, see <see cref="SwitchMappings"/>, or
/// <c>--ConfigDirectory=/etc/bot</c>). Command line arguments take precedence.
/// </remarks>
public sealed class BotRunnerOptions
{
    /// <summary>
    /// Prefix of the environment variables, which is removed before binding (<c>BOT_READYFILE</c> →
    /// <see cref="ReadyFile"/>).
    /// </summary>
    public const string EnvironmentPrefix = "BOT_";

    /// <summary>
    /// Environment variable passed to MCC, read by the bundled status ChatBot (<c>Scripts/KobblestoneStatus.cs</c>).
    /// </summary>
    public const string ReadyFileEnvironmentVariable = "KOBBLESTONE_READY_FILE";

    /// <summary>
    /// Short command line switches.
    /// </summary>
    public static readonly IDictionary<string, string> SwitchMappings = new Dictionary<string, string>
    {
        ["--config-dir"] = nameof(ConfigDirectory),
        ["--scripts-dir"] = nameof(ScriptsDirectory),
        ["--session-dir"] = nameof(SessionDirectory),
        ["--work-dir"] = nameof(WorkingDirectory),
        ["--mcc"] = nameof(MccExecutable),
        ["--ready-file"] = nameof(ReadyFile),
        ["--shutdown-timeout"] = nameof(ShutdownTimeout),
        ["--termination-message-path"] = nameof(TerminationMessagePath),
    };

    /// <summary>
    /// Directory with the MCC configuration layers (<c>*.toml</c>), merged in ordinal order.
    /// </summary>
    public string ConfigDirectory { get; set; } = "/etc/kobblestone/bot/config";

    /// <summary>
    /// Directory with the bot's scripts, copied into <see cref="WorkingDirectory"/>.
    /// </summary>
    public string ScriptsDirectory { get; set; } = "/etc/kobblestone/bot/scripts";

    /// <summary>
    /// Directory where the session `Secret` of the `Account` is mounted. Missing for offline bots.
    /// </summary>
    public string SessionDirectory { get; set; } = "/etc/kobblestone/session";

    /// <summary>
    /// MCC's working directory (configuration, session cache, scripts).
    /// </summary>
    public string WorkingDirectory { get; set; } = "/data";

    /// <summary>
    /// Path of the MCC executable.
    /// </summary>
    public string MccExecutable { get; set; } = "/opt/mcc/MinecraftClient";

    /// <summary>
    /// File that exists while the bot is in the game; checked by the readiness probe.
    /// </summary>
    public string ReadyFile { get; set; } = "/tmp/kobblestone-ready";

    /// <summary>
    /// How long MCC may take to disconnect after SIGTERM before it is killed.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// File the reason MCC exited is written to. Kubernetes shows it as the container's termination message, and the
    /// operator reports it in the `Bot` status.
    /// </summary>
    public string TerminationMessagePath { get; set; } = "/dev/termination-log";

    public static string Usage =>
        $"""
            Usage: Kobblestone.BotRunner [options]

            Prepares Minecraft-Console-Client's working directory (configuration, session, scripts), runs it and
            reports why it exited.

            Options (command line, environment variable):
                  --config-dir <dir>                {EnvironmentPrefix}CONFIGDIRECTORY         Default: /etc/kobblestone/bot/config.
                  --scripts-dir <dir>               {EnvironmentPrefix}SCRIPTSDIRECTORY        Default: /etc/kobblestone/bot/scripts.
                  --session-dir <dir>               {EnvironmentPrefix}SESSIONDIRECTORY        Default: /etc/kobblestone/session.
                  --work-dir <dir>                  {EnvironmentPrefix}WORKINGDIRECTORY        Default: /data.
                  --mcc <path>                      {EnvironmentPrefix}MCCEXECUTABLE           Default: /opt/mcc/MinecraftClient.
                  --ready-file <path>               {EnvironmentPrefix}READYFILE               Default: /tmp/kobblestone-ready.
                  --shutdown-timeout <hh:mm:ss>     {EnvironmentPrefix}SHUTDOWNTIMEOUT         Default: 00:00:10.
                  --termination-message-path <path> {EnvironmentPrefix}TERMINATIONMESSAGEPATH  Default: /dev/termination-log.
              -h, --help                            Show this help.
            """;
}

public sealed class BotRunnerOptionsValidation : IValidateOptions<BotRunnerOptions>
{
    public ValidateOptionsResult Validate(string? name, BotRunnerOptions options)
    {
        var failures = new List<string>();

        foreach (
            var (option, value) in new[]
            {
                (nameof(options.ConfigDirectory), options.ConfigDirectory),
                (nameof(options.ScriptsDirectory), options.ScriptsDirectory),
                (nameof(options.SessionDirectory), options.SessionDirectory),
                (nameof(options.WorkingDirectory), options.WorkingDirectory),
                (nameof(options.MccExecutable), options.MccExecutable),
                (nameof(options.ReadyFile), options.ReadyFile),
                (nameof(options.TerminationMessagePath), options.TerminationMessagePath),
            }
        )
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"{option} must not be empty.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.MccExecutable) && !File.Exists(options.MccExecutable))
        {
            failures.Add($"MCC executable {options.MccExecutable} does not exist.");
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.ShutdownTimeout)} must be positive.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
