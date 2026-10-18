using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Kobblestone.Sessions;

using Microsoft.Extensions.Options;

namespace Kobblestone.AccountAuth;

/// <summary>
/// Hand-written Microsoft → Xbox Live → Minecraft authorization chain using the login.live.com device code flow.
/// </summary>
/// <remarks>
/// TODO(verify): Not tested against the live services yet. Points to verify are marked with TODO(verify).
/// See https://minecraft.wiki/w/Microsoft_authentication for the individual steps.
/// </remarks>
public sealed class XboxLiveAuthenticator(HttpClient http, IOptions<AccountAuthOptions> options)
    : IMinecraftAuthenticator
{
    private readonly string _clientId = options.Value.ClientId;

    private readonly bool _fetchProfileKey = options.Value.FetchProfileKey;

    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";

    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    private const string XboxUserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";

    private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";

    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private const string MinecraftCertificatesUrl = "https://api.minecraftservices.com/player/certificates";

    /// <summary>
    /// TODO(verify): login.live.com tokens are passed with <c>t=</c>, tokens of Azure apps (login.microsoftonline.com)
    /// with <c>d=</c>.
    /// </summary>
    private const string RpsTicketPrefix = "t=";

    public async Task<DeviceCodeRequest> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var response = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code"
        }, cancellationToken).ConfigureAwait(false);

        var userCode = response.GetString("user_code");
        var verificationUri = response.GetString("verification_uri");

        return new DeviceCodeRequest(
            response.GetString("device_code"),
            TimeSpan.FromSeconds(response["interval"]?.GetValue<int>() ?? 5),
            new DeviceFlow(
                userCode,
                verificationUri,
                DateTimeOffset.UtcNow.AddSeconds(response["expires_in"]?.GetValue<int>() ?? 900),
                $"To sign in, open {verificationUri} and enter the code {userCode}."));
    }

    public async Task<AuthorizationResult> CompleteDeviceCodeAsync(DeviceCodeRequest request,
        CancellationToken cancellationToken)
    {
        var interval = request.PollingInterval;

        while (DateTimeOffset.UtcNow < request.Flow.ExpiresAt)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _clientId,
                ["device_code"] = request.DeviceCode
            }), cancellationToken).ConfigureAwait(false);

            var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return await LoginAsync(body.GetString("access_token"), body.GetString("refresh_token"),
                    cancellationToken).ConfigureAwait(false);
            }

            switch (body["error"]?.GetValue<string>())
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Device code flow failed: {body["error"]} {body["error_description"]}");
            }
        }

        throw new TimeoutException("The device code expired before the authorization was completed.");
    }

    public async Task<AuthorizationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId,
            ["scope"] = Scope,
            ["refresh_token"] = refreshToken
        }), cancellationToken).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new RefreshTokenRejectedException($"Refresh token rejected: {body["error"]} {body["error_description"]}");
        }

        response.EnsureSuccessStatusCode();

        return await LoginAsync(body.GetString("access_token"),
            body["refresh_token"]?.GetValue<string>() ?? refreshToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthorizationResult> LoginAsync(string msaAccessToken, string refreshToken,
        CancellationToken cancellationToken)
    {
        var xbl = await PostJsonAsync(XboxUserAuthUrl, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = RpsTicketPrefix + msaAccessToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        }, cancellationToken).ConfigureAwait(false);

        using var xstsResponse = await http.PostAsJsonAsync(XstsAuthUrl, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xbl.GetString("Token"))
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT"
        }, cancellationToken).ConfigureAwait(false);

        var xsts = await ReadJsonAsync(xstsResponse, cancellationToken).ConfigureAwait(false);

        if (xstsResponse.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new AccountNotUsableException(DescribeXstsError(xsts["XErr"]?.GetValue<long>()));
        }

        xstsResponse.EnsureSuccessStatusCode();

        var userHash = xsts["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("XSTS response did not contain a user hash.");

        var minecraft = await PostJsonAsync(MinecraftLoginUrl,
            new JsonObject { ["identityToken"] = $"XBL3.0 x={userHash};{xsts.GetString("Token")}" },
            cancellationToken).ConfigureAwait(false);

        var accessToken = minecraft.GetString("access_token");
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddSeconds(minecraft["expires_in"]?.GetValue<int>() ?? 86400);

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var profileResponse = await http.SendAsync(profileRequest, cancellationToken).ConfigureAwait(false);

        if (profileResponse.StatusCode is HttpStatusCode.NotFound)
        {
            throw new AccountNotUsableException(
                "The account has no Minecraft: Java Edition profile (game not owned, or Game Pass users have to " +
                "start the official launcher once).");
        }

        var profile = await ReadJsonAsync(profileResponse.EnsureSuccessStatusCode(), cancellationToken)
            .ConfigureAwait(false);

        var profileKey = _fetchProfileKey
            ? await FetchProfileKeyAsync(accessToken, cancellationToken).ConfigureAwait(false)
            : null;

        var session = new MinecraftSession(
            profile.GetString("name"),
            Guid.Parse(profile.GetString("id")),
            accessToken,
            issuedAt,
            expiresAt,
            Xuid: null,
            profileKey);

        return new AuthorizationResult(session, refreshToken);
    }

    private async Task<ProfileKey?> FetchProfileKeyAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MinecraftCertificatesUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Chat signing is optional. Without a key pair, bots can still join servers that do not enforce secure chat.
            Console.Error.WriteLine($"Could not fetch profile key pair: HTTP {(int)response.StatusCode}");

            return null;
        }

        var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        return new ProfileKey(
            body["keyPair"]?["privateKey"]?.GetValue<string>() ?? throw new InvalidOperationException("No private key."),
            body["keyPair"]?["publicKey"]?.GetValue<string>() ?? throw new InvalidOperationException("No public key."),
            body["publicKeySignature"]?.GetValue<string>(),
            body.GetString("publicKeySignatureV2"),
            DateTimeOffset.Parse(body.GetString("expiresAt")),
            DateTimeOffset.Parse(body.GetString("refreshedAfter")));
    }

    private static string DescribeXstsError(long? error) => error switch
    {
        2148916233 => "The Microsoft account has no Xbox profile. Sign in to minecraft.net once to create one.",
        2148916235 => "Xbox Live is not available in the account's country.",
        2148916236 or 2148916237 => "The account needs adult verification on the Xbox homepage.",
        2148916238 => "The account belongs to a child and must be added to a family.",
        2148916227 => "The account is banned from Xbox Live.",
        _ => $"Xbox Live rejected the account (XErr {error})."
    };

    private async Task<JsonNode> PostFormAsync(string url, Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);

        return await ReadJsonAsync(response.EnsureSuccessStatusCode(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode> PostJsonAsync(string url, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);

        return await ReadJsonAsync(response.EnsureSuccessStatusCode(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(content) ? new JsonObject() : JsonNode.Parse(content) ?? new JsonObject();
    }
}

internal static class JsonNodeExtensions
{
    public static string GetString(this JsonNode node, string property) =>
        node[property]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Response did not contain '{property}'.");
}
