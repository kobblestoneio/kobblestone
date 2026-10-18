using k8s;
using Kobblestone.AccountAuth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Authorizes an `Account` once and exits (see AccountAuthorizer). The operator derives the `Account` status from its
// Secrets, and from this container's exit code and termination message.
//
// Exit codes: 0 authorized, 1 failed (retried later), 2 the account cannot be used for Minecraft: Java Edition,
// 64 invalid configuration.

if (args.Any(a => a is "-h" or "--help"))
{
    Console.WriteLine(AccountAuthOptions.Usage);

    return 0;
}

var builder = Host.CreateEmptyApplicationBuilder(
    new HostApplicationBuilderSettings { Args = args }
);

// Later sources win: command line arguments override environment variables.
builder
    .Configuration.AddEnvironmentVariables(AccountAuthOptions.EnvironmentPrefix)
    .AddCommandLine(args, AccountAuthOptions.SwitchMappings);

builder.Services.AddOptions<AccountAuthOptions>().Bind(builder.Configuration);

builder
    .Services.AddSingleton<IPostConfigureOptions<AccountAuthOptions>, AccountAuthOptionsDefaults>()
    .AddSingleton<IValidateOptions<AccountAuthOptions>, AccountAuthOptionsValidation>()
    // In-cluster service account, or else KUBECONFIG / ~/.kube/config.
    .AddSingleton(_ => KubernetesClientConfiguration.BuildDefaultConfig())
    .AddSingleton<IKubernetes>(sp => new Kubernetes(
        sp.GetRequiredService<KubernetesClientConfiguration>()
    ))
    .AddSingleton(_ =>
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kobblestone.AccountAuth");

        return http;
    })
    .AddSingleton<IMinecraftAuthenticator, XboxLiveAuthenticator>()
    .AddSingleton<AccountSecretStore>()
    .AddSingleton<AccountAuthorizer>();

using var host = builder.Build();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// Resolved before the options, so failures to bind them can be reported too.
var terminationMessagePath =
    builder.Configuration[nameof(AccountAuthOptions.TerminationMessagePath)]
    ?? new AccountAuthOptions().TerminationMessagePath;

AccountAuthOptions options;

try
{
    // Binds, completes and validates the options.
    options = host.Services.GetRequiredService<IOptions<AccountAuthOptions>>().Value;
}
catch (Exception e)
{
    // Missing or invalid options, e.g. a value that cannot be converted or no Kubernetes configuration.
    Console.Error.WriteLine(
        e is OptionsValidationException validation
            ? string.Join(Environment.NewLine, validation.Failures)
            : e.Message
    );
    Console.Error.WriteLine();
    Console.Error.WriteLine(AccountAuthOptions.Usage);

    await WriteTerminationMessageAsync(e.Message);

    return 64;
}

try
{
    Console.WriteLine($"Authorizing session Secret {options.Namespace}/{options.SessionSecretName}.");

    var result = await host
        .Services.GetRequiredService<AccountAuthorizer>()
        .RunAsync(cancellation.Token);

    Console.WriteLine(
        $"Authorized as {result.Session.Username} ({result.Session.Uuid}), session expires at {result.Session.ExpiresAt:O}."
    );

    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);

    await WriteTerminationMessageAsync(e.Message);

    return e is AccountNotUsableException ? 2 : 1;
}

// Read by the operator to explain why the `Account` is not authorized.
async Task WriteTerminationMessageAsync(string message)
{
    try
    {
        await File.WriteAllTextAsync(terminationMessagePath, message);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        // Not running in a container (e.g. locally).
    }
}
