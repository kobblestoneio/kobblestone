using System.Diagnostics;
using System.Runtime.InteropServices;
using Kobblestone.Sessions;
using Microsoft.Extensions.Options;
using Tomlyn.Model;

namespace Kobblestone.BotRunner;

/// <summary>
/// Runs Minecraft-Console-Client once:
/// <list type="number">
/// <item>Merges the configuration layers into <c>MinecraftClient.ini</c>.</item>
/// <item>Converts the mounted standard session (if any) into MCC's cache files.</item>
/// <item>Copies the bot's scripts (and the bundled status script) into MCC's working directory.</item>
/// <item>Runs MCC non-interactively, forwards its output and reports why it exited via the termination message.</item>
/// </list>
/// </summary>
public sealed class MccRunner(IOptions<BotRunnerOptions> options)
{
    private const string StatusScript = "KobblestoneStatus.cs";

    /// <summary>
    /// Number of MCC output lines included in the termination message.
    /// </summary>
    private const int RecentLines = 5;

    private readonly BotRunnerOptions _options = options.Value;

    /// <summary>
    /// Returns the exit code of the container and the reason, written to the termination message by the caller.
    /// </summary>
    public async Task<(int ExitCode, string Message)> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.WorkingDirectory);
        File.Delete(_options.ReadyFile);

        var config = ConfigMerger.MergeDirectory(_options.ConfigDirectory);

        var session = SessionSecret.ReadFromDirectory(_options.SessionDirectory);

        if (session is not null)
        {
            if (session.IsExpired(DateTimeOffset.UtcNow))
            {
                return (
                    4,
                    $"The session of {session.Username} expired at {session.ExpiresAt:O}. Waiting for the Account to refresh it."
                );
            }

            // The login is only the key MCC uses for its session cache; it does not have to be the e-mail address.
            var general = GetOrAddTable(GetOrAddTable(config, "Main"), "General");
            general["Account"] = new TomlTable { ["Login"] = session.Username, ["Password"] = string.Empty };
            general["AccountType"] = "microsoft";

            MccSessionCache.Write(_options.WorkingDirectory, session.Username, session);
        }

        AddStatusScriptTask(config);

        var iniPath = Path.Combine(_options.WorkingDirectory, "MinecraftClient.ini");
        await File.WriteAllTextAsync(iniPath, ConfigMerger.Serialize(config), cancellationToken)
            .ConfigureAwait(false);

        CopyScripts();

        return await RunMccAsync(iniPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the bundled status ChatBot on the first login (it survives reconnects within the same MCC process).
    /// </summary>
    private static void AddStatusScriptTask(TomlTable config)
    {
        var scheduler = GetOrAddTable(GetOrAddTable(config, "ChatBot"), "ScriptScheduler");
        scheduler["Enabled"] = true;

        if (!scheduler.TryGetValue("TaskList", out var taskList) || taskList is not TomlTableArray tasks)
        {
            scheduler["TaskList"] = tasks = new TomlTableArray();
        }

        tasks.Add(
            new TomlTable
            {
                ["Task_Name"] = "kobblestone-status",
                ["Trigger_On_First_Login"] = true,
                ["Action"] = $"script {StatusScript}",
            }
        );
    }

    private void CopyScripts()
    {
        if (Directory.Exists(_options.ScriptsDirectory))
        {
            foreach (var script in Directory.GetFiles(_options.ScriptsDirectory))
            {
                File.Copy(
                    script,
                    Path.Combine(_options.WorkingDirectory, Path.GetFileName(script)),
                    overwrite: true
                );
            }
        }

        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Scripts", StatusScript),
            Path.Combine(_options.WorkingDirectory, StatusScript),
            overwrite: true
        );
    }

    private async Task<(int ExitCode, string Message)> RunMccAsync(string iniPath, CancellationToken cancellationToken)
    {
        using var mcc = new Process
        {
            StartInfo = new ProcessStartInfo(_options.MccExecutable)
            {
                WorkingDirectory = _options.WorkingDirectory,
                // The config file has to be the first, BasicIO-NoColor the last argument.
                ArgumentList = { iniPath, "BasicIO-NoColor" },
                Environment = { [BotRunnerOptions.ReadyFileEnvironmentVariable] = _options.ReadyFile },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        var recent = new Queue<string>();

        void Forward(string? line, TextWriter target)
        {
            if (line is null)
            {
                return;
            }

            target.WriteLine(line);

            lock (recent)
            {
                recent.Enqueue(line);

                while (recent.Count > RecentLines)
                {
                    recent.Dequeue();
                }
            }
        }

        mcc.OutputDataReceived += (_, e) => Forward(e.Data, Console.Out);
        mcc.ErrorDataReceived += (_, e) => Forward(e.Data, Console.Error);

        mcc.Start();
        mcc.BeginOutputReadLine();
        mcc.BeginErrorReadLine();

        // Kubernetes stops the Pod with SIGTERM: ask MCC to disconnect cleanly, kill it if it does not exit in time.
        using var sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;

                Stop(mcc);
            }
        );

        // Stops MCC the same way when the runner itself is cancelled.
        await using var cancellationRegistration = cancellationToken.Register(() => Stop(mcc));

        await mcc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        File.Delete(_options.ReadyFile);

        string lines;

        lock (recent)
        {
            lines = string.Join('\n', recent);
        }

        // Exit codes of MCC in non-interactive mode.
        var reason = mcc.ExitCode switch
        {
            0 => "MCC exited.",
            2 => "Kicked from the server.",
            3 => "Connection to the server lost.",
            4 => "Login rejected.",
            _ => $"MCC failed with exit code {mcc.ExitCode}.",
        };

        return (mcc.ExitCode, $"{reason}\n{lines}".Trim());
    }

    private void Stop(Process mcc)
    {
        try
        {
            mcc.StandardInput.WriteLine("/quit");
            mcc.StandardInput.Flush();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // MCC already exited.
        }

        try
        {
            if (!mcc.WaitForExit(_options.ShutdownTimeout))
            {
                mcc.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // MCC already exited.
        }
    }

    private static TomlTable GetOrAddTable(TomlTable parent, string key)
    {
        if (parent.TryGetValue(key, out var value) && value is TomlTable table)
        {
            return table;
        }

        table = new TomlTable();
        parent[key] = table;

        return table;
    }
}
