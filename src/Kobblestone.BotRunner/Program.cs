using Kobblestone.BotRunner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Entrypoint of the bot image, see MccRunner. The exit code is MCC's (2 kicked, 3 connection lost, 4 login rejected or
// session expired), 1 for other failures and 64 for invalid options. The reason is written to the termination message,
// which the operator shows in the `Bot` status.

if (args.Any(a => a is "-h" or "--help"))
{
    Console.WriteLine(BotRunnerOptions.Usage);

    return 0;
}

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

// Later sources win: command line arguments override environment variables.
builder
    .Configuration.AddEnvironmentVariables(BotRunnerOptions.EnvironmentPrefix)
    .AddCommandLine(args, BotRunnerOptions.SwitchMappings);

builder.Services.AddOptions<BotRunnerOptions>().Bind(builder.Configuration);

builder
    .Services.AddSingleton<IValidateOptions<BotRunnerOptions>, BotRunnerOptionsValidation>()
    .AddSingleton<MccRunner>();

using var host = builder.Build();

// Resolved before the options, so failures to bind them can be reported too.
var terminationMessagePath =
    builder.Configuration[nameof(BotRunnerOptions.TerminationMessagePath)]
    ?? new BotRunnerOptions().TerminationMessagePath;

try
{
    // Binds and validates the options.
    _ = host.Services.GetRequiredService<IOptions<BotRunnerOptions>>().Value;
}
catch (Exception e)
{
    Console.Error.WriteLine(
        e is OptionsValidationException validation ? string.Join(Environment.NewLine, validation.Failures) : e.Message
    );
    Console.Error.WriteLine();
    Console.Error.WriteLine(BotRunnerOptions.Usage);

    return Exit(64, e.Message);
}

try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };

    var (exitCode, message) = await host.Services.GetRequiredService<MccRunner>().RunAsync(cancellation.Token);

    return Exit(exitCode, message);
}
catch (Exception e)
{
    Console.Error.WriteLine(e);

    return Exit(1, e.Message);
}

int Exit(int exitCode, string message)
{
    Console.Error.WriteLine(message);

    try
    {
        // Read by the operator for the `Bot` status. Kubernetes truncates it to 4096 bytes.
        File.WriteAllText(terminationMessagePath, message);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        // Not running in a container (e.g. locally).
    }

    return exitCode;
}
