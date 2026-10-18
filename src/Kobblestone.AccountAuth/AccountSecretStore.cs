using System.Text.Json;

using k8s;
using k8s.Models;

using Kobblestone.Sessions;

using Microsoft.Extensions.Options;

namespace Kobblestone.AccountAuth;

/// <summary>
/// Reads and writes the two `Secret`s of an `Account`. This is the only state the auth `Job` writes: the `Account`
/// status is derived from these `Secret`s by the operator.
/// </summary>
public sealed class AccountSecretStore(IKubernetes client, IOptions<AccountAuthOptions> options)
{
    private readonly AccountAuthOptions _options = options.Value;

    public async Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken)
    {
        var secret = await client.CoreV1
            .ReadNamespacedSecretAsync(_options.CredentialsSecretName, _options.Namespace,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return CredentialsSecret.ReadRefreshToken(secret.Data);
    }

    public Task WriteDeviceFlowAsync(DeviceFlow deviceFlow, CancellationToken cancellationToken) =>
        PatchCredentialsAsync(new Dictionary<string, object?>
        {
            ["stringData"] = new Dictionary<string, string>
            {
                [CredentialsSecret.DeviceFlowJson] = CredentialsSecret.Serialize(deviceFlow)
            }
        }, cancellationToken);

    /// <summary>
    /// Stores a new session and refresh token and removes a pending device flow.
    /// </summary>
    public async Task WriteAuthorizationAsync(AuthorizationResult result, CancellationToken cancellationToken)
    {
        // The session Secret is replaced as a whole, so it always contains exactly the standard keys (this also
        // removes keys written by earlier versions, e.g. the prismarine-auth caches).
        var secret = await client.CoreV1
            .ReadNamespacedSecretAsync(_options.SessionSecretName, _options.Namespace,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        secret.Data = null;
        secret.StringData = SessionSecret.ToStringData(result.Session);

        await client.CoreV1
            .ReplaceNamespacedSecretAsync(secret, _options.SessionSecretName, _options.Namespace,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await PatchCredentialsAsync(new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?> { [CredentialsSecret.DeviceFlowJson] = null },
            ["stringData"] = new Dictionary<string, string> { [CredentialsSecret.RefreshToken] = result.RefreshToken }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a stored refresh token (e.g. after it was rejected) and any pending device flow.
    /// </summary>
    public Task ClearCredentialsAsync(CancellationToken cancellationToken) =>
        PatchCredentialsAsync(new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?>
            {
                [CredentialsSecret.RefreshToken] = null,
                [CredentialsSecret.DeviceFlowJson] = null
            }
        }, cancellationToken);

    private async Task PatchCredentialsAsync(Dictionary<string, object?> patch, CancellationToken cancellationToken)
    {
        await client.CoreV1
            .PatchNamespacedSecretAsync(
                new V1Patch(JsonSerializer.Serialize(patch), V1Patch.PatchType.MergePatch),
                _options.CredentialsSecretName,
                _options.Namespace,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
