using Kobblestone.Sessions;

namespace Kobblestone.AccountAuth;

/// <summary>
/// Obtains Minecraft: Java Edition sessions for a Microsoft account.
/// </summary>
/// <remarks>
/// The implementation is exchangeable (e.g. hand-written HTTP chain or a library such as
/// <c>CmlLib.Core.Auth.Microsoft</c>); the rest of the CLI only depends on this interface.
/// </remarks>
public interface IMinecraftAuthenticator
{
    /// <summary>
    /// Starts a device code flow. The returned <see cref="DeviceCodeRequest.Flow"/> is shown to the user.
    /// </summary>
    Task<DeviceCodeRequest> RequestDeviceCodeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits until the user completed the device code flow, then obtains a session.
    /// </summary>
    Task<AuthorizationResult> CompleteDeviceCodeAsync(DeviceCodeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Obtains a new session with a refresh token from an earlier authorization.
    /// </summary>
    /// <exception cref="RefreshTokenRejectedException">The refresh token is no longer valid.</exception>
    Task<AuthorizationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <param name="DeviceCode">Opaque code used for polling, never shown to the user.</param>
/// <param name="PollingInterval">Interval the token endpoint asks to be polled at.</param>
/// <param name="Flow">What the user needs to complete the authorization.</param>
public sealed record DeviceCodeRequest(string DeviceCode, TimeSpan PollingInterval, DeviceFlow Flow);

/// <param name="Session">The new session.</param>
/// <param name="RefreshToken">Refresh token for the next refresh (may be rotated).</param>
public sealed record AuthorizationResult(MinecraftSession Session, string RefreshToken);

public sealed class RefreshTokenRejectedException(string message) : Exception(message);

/// <summary>
/// The account cannot be used for Minecraft: Java Edition, independent of the authorization (e.g. no Xbox profile, no
/// game ownership). Retrying does not help.
/// </summary>
public sealed class AccountNotUsableException(string message) : Exception(message);
