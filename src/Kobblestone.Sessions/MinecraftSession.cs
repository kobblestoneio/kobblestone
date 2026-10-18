namespace Kobblestone.Sessions;

/// <summary>
/// An authorized Minecraft: Java Edition session, independent of any client implementation.
/// </summary>
/// <param name="Username">Minecraft profile name.</param>
/// <param name="Uuid">Minecraft profile UUID (with dashes).</param>
/// <param name="AccessToken">Minecraft access token (JWT) for the session server and Minecraft services.</param>
/// <param name="IssuedAt">When the session was obtained.</param>
/// <param name="ExpiresAt">Expiry of <paramref name="AccessToken"/>.</param>
/// <param name="Xuid">Xbox user ID, if known.</param>
/// <param name="ProfileKey">Key pair for signing chat messages, if fetched.</param>
public sealed record MinecraftSession(
    string Username,
    Guid Uuid,
    string AccessToken,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? Xuid = null,
    ProfileKey? ProfileKey = null)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>
/// Chat signing key pair as issued by <c>POST https://api.minecraftservices.com/player/certificates</c>.
/// </summary>
/// <param name="PrivateKeyPem">Private key in PEM format, exactly as returned by Mojang.</param>
/// <param name="PublicKeyPem">Public key in PEM format, exactly as returned by Mojang.</param>
/// <param name="PublicKeySignature">Base64 <c>publicKeySignature</c> (legacy, for 1.19.0).</param>
/// <param name="PublicKeySignatureV2">Base64 <c>publicKeySignatureV2</c>.</param>
/// <param name="ExpiresAt">Expiry of the key pair.</param>
/// <param name="RefreshedAfter">Time after which Mojang recommends fetching a new key pair.</param>
public sealed record ProfileKey(
    string PrivateKeyPem,
    string PublicKeyPem,
    string? PublicKeySignature,
    string PublicKeySignatureV2,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RefreshedAfter);

/// <summary>
/// A pending OAuth device authorization the user has to complete.
/// </summary>
public sealed record DeviceFlow(
    string UserCode,
    string VerificationUri,
    DateTimeOffset ExpiresAt,
    string? Message);
