namespace Kobblestone.AccountAuth;

/// <summary>
/// Authorizes an `Account` once:
/// <list type="bullet">
/// <item>With a stored refresh token, a new session is obtained silently.</item>
/// <item>Otherwise (or if the refresh token was rejected), a device code flow is started and the user has to complete
/// it.</item>
/// </list>
/// The result is written to the `Account`'s `Secret`s only.
/// </summary>
public sealed class AccountAuthorizer(AccountSecretStore store, IMinecraftAuthenticator authenticator)
{
    public async Task<AuthorizationResult> RunAsync(CancellationToken cancellationToken)
    {
        AuthorizationResult? result = null;

        if (await store.GetRefreshTokenAsync(cancellationToken).ConfigureAwait(false) is { } refreshToken)
        {
            try
            {
                result = await authenticator.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);

                Console.WriteLine("Session refreshed.");
            }
            catch (RefreshTokenRejectedException e)
            {
                Console.WriteLine($"{e.Message} Starting a new device code flow.");

                await store.ClearCredentialsAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (result is null)
        {
            var request = await authenticator.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);

            await store.WriteDeviceFlowAsync(request.Flow, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(request.Flow.Message);

            result = await authenticator.CompleteDeviceCodeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        await store.WriteAuthorizationAsync(result, cancellationToken).ConfigureAwait(false);

        return result;
    }
}
