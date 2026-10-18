using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kobblestone.Sessions;

/// <summary>
/// The standard format of an `Account`'s session `Secret` (<c>&lt;account&gt;</c>, type
/// <see cref="Type"/>). It is the contract between `Account`s and any workload consuming them, so it must only ever be
/// extended in a backwards compatible way.
/// </summary>
public static class SessionSecret
{
    public const string Type = "kobblestone.io/minecraft-session";

    /// <summary>All fields in one JSON document (camelCase).</summary>
    public const string SessionJson = "session.json";

    public const string Username = "username";

    public const string Uuid = "uuid";

    public const string AccessToken = "access-token";

    public const string IssuedAt = "issued-at";

    public const string ExpiresAt = "expires-at";

    public const string Xuid = "xuid";

    public const string ProfileKeyPrivatePem = "profile-key-private.pem";

    public const string ProfileKeyPublicPem = "profile-key-public.pem";

    public const string ProfileKeySignature = "profile-key-signature";

    public const string ProfileKeySignatureV2 = "profile-key-signature-v2";

    public const string ProfileKeyExpiresAt = "profile-key-expires-at";

    public const string ProfileKeyRefreshedAfter = "profile-key-refreshed-after";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>
    /// Renders a session as the complete string data of the session `Secret`.
    /// </summary>
    public static IDictionary<string, string> ToStringData(MinecraftSession session)
    {
        var data = new Dictionary<string, string>
        {
            [SessionJson] = JsonSerializer.Serialize(session, JsonOptions),
            [Username] = session.Username,
            [Uuid] = session.Uuid.ToString("D"),
            [AccessToken] = session.AccessToken,
            [IssuedAt] = Format(session.IssuedAt),
            [ExpiresAt] = Format(session.ExpiresAt)
        };

        if (session.Xuid is { } xuid)
        {
            data[Xuid] = xuid;
        }

        if (session.ProfileKey is { } key)
        {
            data[ProfileKeyPrivatePem] = key.PrivateKeyPem;
            data[ProfileKeyPublicPem] = key.PublicKeyPem;
            data[ProfileKeySignatureV2] = key.PublicKeySignatureV2;
            data[ProfileKeyExpiresAt] = Format(key.ExpiresAt);
            data[ProfileKeyRefreshedAfter] = Format(key.RefreshedAfter);

            if (key.PublicKeySignature is { } signature)
            {
                data[ProfileKeySignature] = signature;
            }
        }

        return data;
    }

    /// <summary>
    /// Reads a session from the data of a session `Secret` (raw bytes as returned by the API server).
    /// Returns <c>null</c> if the `Secret` does not contain a session (yet).
    /// </summary>
    public static MinecraftSession? Read(IDictionary<string, byte[]>? data)
    {
        if (data is null || !data.TryGetValue(SessionJson, out var json) || json.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MinecraftSession>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a session from a directory where the session `Secret` is mounted as files.
    /// </summary>
    public static MinecraftSession? ReadFromDirectory(string directory)
    {
        var path = Path.Combine(directory, SessionJson);

        return File.Exists(path)
            ? Read(new Dictionary<string, byte[]> { [SessionJson] = File.ReadAllBytes(path) })
            : null;
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// The internal credentials `Secret` of an `Account` (<c>&lt;account&gt;-credentials</c>). Never mounted into
/// workloads: it allows minting new sessions.
/// </summary>
public static class CredentialsSecret
{
    public const string Suffix = "-credentials";

    /// <summary>Microsoft refresh token.</summary>
    public const string RefreshToken = "refresh-token";

    /// <summary>Pending device flow (<see cref="DeviceFlow"/> as JSON) while waiting for the user.</summary>
    public const string DeviceFlowJson = "device-flow.json";

    public static string NameFor(string accountName) => accountName + Suffix;

    public static string? ReadRefreshToken(IDictionary<string, byte[]>? data) =>
        data is not null && data.TryGetValue(RefreshToken, out var token) && token.Length > 0
            ? Encoding.UTF8.GetString(token)
            : null;

    public static DeviceFlow? ReadDeviceFlow(IDictionary<string, byte[]>? data)
    {
        if (data is null || !data.TryGetValue(DeviceFlowJson, out var json) || json.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceFlow>(json, SessionSecret.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(DeviceFlow deviceFlow) =>
        JsonSerializer.Serialize(deviceFlow, SessionSecret.JsonOptions);
}

/// <summary>
/// Labels shared between the operator and the auth `Job`.
/// </summary>
public static class AccountLabels
{
    /// <summary>Set on both `Secret`s and the auth `Job` of an `Account`, value is the `Account` name.</summary>
    public const string Account = "kobblestone.io/account";
}
