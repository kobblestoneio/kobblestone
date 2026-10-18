using k8s;
using Microsoft.Extensions.Options;

namespace Kobblestone.AccountAuth;

/// <summary>
/// Configuration of a single run.
/// </summary>
/// <remarks>
/// Bound from environment variables with the prefix <see cref="EnvironmentPrefix"/> (e.g. <c>AUTH_SESSIONSECRETNAME</c>)
/// and command line arguments (e.g. <c>--session-secret my-account</c>, see <see cref="SwitchMappings"/>, or
/// <c>--SessionSecretName=my-account</c>). Command line arguments take precedence.
/// </remarks>
public sealed class AccountAuthOptions
{
    /// <summary>
    /// Prefix of the environment variables, which is removed before binding (<c>AUTH_NAMESPACE</c> →
    /// <see cref="Namespace"/>).
    /// </summary>
    public const string EnvironmentPrefix = "AUTH_";

    /// <summary>
    /// Default OAuth client used for the Microsoft device code flow.
    /// </summary>
    /// <remarks>
    /// TODO(verify): Must be a client that is allowed to use the device code flow on login.live.com and whose tokens
    /// are accepted by Xbox Live for the Minecraft relying party. Self-registered Azure apps additionally need
    /// Mojang's approval for the Minecraft API (https://aka.ms/mce-reviewappid). See design doc 0001, open decision 1.
    /// </remarks>
    public const string DefaultClientId = "00000000402b5328";

    /// <summary>
    /// Short command line switches.
    /// </summary>
    public static readonly IDictionary<string, string> SwitchMappings = new Dictionary<
        string,
        string
    >
    {
        ["-n"] = nameof(Namespace),
        ["--namespace"] = nameof(Namespace),
        ["--session-secret"] = nameof(SessionSecretName),
        ["--credentials-secret"] = nameof(CredentialsSecretName),
        ["--client-id"] = nameof(ClientId),
        ["--fetch-profile-key"] = nameof(FetchProfileKey),
        ["--termination-message-path"] = nameof(TerminationMessagePath),
    };

    /// <summary>
    /// Namespace of the `Secret`s. Defaults to the namespace of the in-cluster service account or the current
    /// kubeconfig context, then <c>default</c>.
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Name of the session `Secret`. Required.
    /// </summary>
    public string SessionSecretName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the credentials `Secret`. Required.
    /// </summary>
    public string CredentialsSecretName { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client ID for the Microsoft device code flow. Defaults to <see cref="DefaultClientId"/>.
    /// </summary>
    public string ClientId { get; set; } = DefaultClientId;

    /// <summary>
    /// Whether to fetch the chat signing key pair of the profile.
    /// </summary>
    public bool FetchProfileKey { get; set; } = true;

    /// <summary>
    /// File the reason of a failure is written to. Kubernetes shows it as the container's termination message, and the
    /// operator reports it in the `Account` status.
    /// </summary>
    public string TerminationMessagePath { get; set; } = "/dev/termination-log";

    public static string Usage =>
        $"""
            Usage: Kobblestone.AccountAuth --session-secret <name> --credentials-secret <name> [options]

            Authorizes a Microsoft account for Minecraft (refresh, or device code flow) and writes the session and
            credentials Secrets.

            Options (command line, environment variable):
                  --session-secret <name>       {EnvironmentPrefix}SESSIONSECRETNAME      Session Secret (required).
                  --credentials-secret <name>   {EnvironmentPrefix}CREDENTIALSSECRETNAME  Credentials Secret (required).
              -n, --namespace <namespace>       {EnvironmentPrefix}NAMESPACE              Namespace of the Secrets.
                  --client-id <id>              {EnvironmentPrefix}CLIENTID               Default: {DefaultClientId}.
                  --fetch-profile-key <bool>    {EnvironmentPrefix}FETCHPROFILEKEY        Default: true.
                  --termination-message-path    {EnvironmentPrefix}TERMINATIONMESSAGEPATH Default: /dev/termination-log.
              -h, --help                        Show this help.

            The Kubernetes API is reached with the in-cluster service account, or else the kubeconfig (KUBECONFIG or
            ~/.kube/config).
            """;
}

/// <summary>
/// Fills in the defaults that depend on the Kubernetes client configuration.
/// </summary>
public sealed class AccountAuthOptionsDefaults(KubernetesClientConfiguration kubernetes)
    : IPostConfigureOptions<AccountAuthOptions>
{
    public void PostConfigure(string? name, AccountAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Namespace))
        {
            options.Namespace = kubernetes.Namespace is { Length: > 0 } ns ? ns : "default";
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            options.ClientId = AccountAuthOptions.DefaultClientId;
        }
    }
}

public sealed class AccountAuthOptionsValidation : IValidateOptions<AccountAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AccountAuthOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SessionSecretName))
        {
            failures.Add(
                $"The session Secret is required (--session-secret or {AccountAuthOptions.EnvironmentPrefix}SESSIONSECRETNAME)."
            );
        }

        if (string.IsNullOrWhiteSpace(options.CredentialsSecretName))
        {
            failures.Add(
                $"The credentials Secret is required (--credentials-secret or {AccountAuthOptions.EnvironmentPrefix}CREDENTIALSSECRETNAME)."
            );
        }

        if (
            options.SessionSecretName == options.CredentialsSecretName
            && !string.IsNullOrWhiteSpace(options.SessionSecretName)
        )
        {
            failures.Add("The session and credentials Secrets must be different.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
