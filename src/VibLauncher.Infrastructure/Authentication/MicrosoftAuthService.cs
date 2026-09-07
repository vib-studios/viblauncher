using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VibLauncher.Core.Accounts;
using VibLauncher.Core.Common;
using VibLauncher.Core.Configuration;
using VibLauncher.Core.Diagnostics;
using VibLauncher.Infrastructure.Networking;

namespace VibLauncher.Infrastructure.Authentication;

/// <inheritdoc cref="IMicrosoftAuthService"/>
/// <remarks>
/// <para>
/// The full chain is Microsoft identity, then Xbox Live, then XSTS, then
/// Minecraft services, which is the flow Microsoft documents for third-party
/// launchers. The device-code grant is used rather than an embedded browser: the
/// user signs in on Microsoft's own page, so no password ever passes through
/// this application.
/// </para>
/// <para>
/// Nothing in this file logs a token. The values move between the four calls and
/// then into <see cref="ITokenStore"/>, and only expiry times and the profile
/// name are ever recorded.
/// </para>
/// </remarks>
public sealed class MicrosoftAuthService : IMicrosoftAuthService
{
    private const string Category = "Accounts";

    /// <summary>The consumers tenant: personal Microsoft accounts, which is what Minecraft uses.</summary>
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxLiveUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>The only scope the launcher asks for, plus the refresh-token scope.</summary>
    private const string Scope = "XboxLive.signin offline_access";

    /// <summary>Where the client id can be set without editing settings.</summary>
    private const string ClientIdEnvironmentVariable = "VIBLAUNCHER_MSA_CLIENT_ID";

    private readonly LauncherHttp _http;
    private readonly ISettingsService _settings;
    private readonly ILauncherLog _log;

    public MicrosoftAuthService(LauncherHttp http, ISettingsService settings, ILauncherLog log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// The Azure application id, from settings or the environment.
    /// </summary>
    /// <remarks>
    /// Deliberately not compiled in. A client id belongs to whoever publishes a
    /// build, and shipping someone else's would put this launcher's sign-ins on
    /// their registration.
    /// </remarks>
    private string? ClientId
    {
        get
        {
            var configured = _settings.Current.MicrosoftClientId;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var fromEnvironment = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable);
            return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
        }
    }

    public bool IsConfigured => ClientId is not null;

    public string? ConfigurationHint => IsConfigured
        ? null
        : "Microsoft sign-in needs an Azure application id. Register an application with the Xbox Live sign-in " +
          "scope and the device-code flow enabled, then paste its client id into Settings, Accounts, or set the " +
          ClientIdEnvironmentVariable + " environment variable. Offline accounts work without this.";

    public async Task<MicrosoftSignIn> SignInAsync(
        Action<DeviceCodePrompt> prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var clientId = RequireClientId();

        _log.Info(Category, "Starting a Microsoft device-code sign-in.");

        var device = await RequestDeviceCodeAsync(clientId, cancellationToken).ConfigureAwait(false);
        prompt(new DeviceCodePrompt(device.UserCode, device.VerificationUri, DateTimeOffset.Now.AddSeconds(device.ExpiresIn)));

        var microsoft = await PollForTokenAsync(clientId, device, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(microsoft, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MicrosoftSignIn?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var clientId = RequireClientId();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        });

        using var response = await _http.Client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // An expired or revoked refresh token is a normal end of life, not a
            // fault. The caller signs in again.
            _log.Info(Category, "The stored Microsoft session is no longer valid. A fresh sign-in is needed.");
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var microsoft = ReadMicrosoftTokens(document.RootElement);

        return await CompleteAsync(microsoft, cancellationToken).ConfigureAwait(false);
    }

    private string RequireClientId() =>
        ClientId ?? throw new InvalidConfigurationException(
            "Microsoft sign-in is not configured yet.",
            ConfigurationHint);

    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        });

        using var response = await _http.Client.PostAsync(DeviceCodeUrl, content, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(
                "Microsoft would not start the sign-in.",
                DescribeIdentityError(json)
                ?? "Check that the configured application id allows the device-code flow.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new DeviceCodeResponse(
            Text(root, "device_code") ?? throw new LauncherException("Microsoft did not return a device code.", null),
            Text(root, "user_code") ?? string.Empty,
            Text(root, "verification_uri") ?? "https://microsoft.com/link",
            root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 900,
            root.TryGetProperty("interval", out var interval) && interval.TryGetInt32(out var value) ? value : 5);
    }

    private async Task<MicrosoftTokens> PollForTokenAsync(
        string clientId,
        DeviceCodeResponse device,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval));

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = device.DeviceCode,
            });

            using var response = await _http.Client.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (response.IsSuccessStatusCode)
            {
                return ReadMicrosoftTokens(root);
            }

            switch (Text(root, "error"))
            {
                case "authorization_pending":
                    continue;

                case "slow_down":
                    // Microsoft asks for a slower poll and will keep refusing
                    // until the caller backs off.
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case "authorization_declined":
                    throw new LauncherException(
                        "The sign-in was declined.",
                        "Nothing was added. Start the sign-in again if that was not intended.");

                case "expired_token":
                    throw new LauncherException(
                        "The sign-in code expired before it was used.",
                        "Start the sign-in again to get a new code.");

                default:
                    throw new LauncherException(
                        "The Microsoft sign-in failed.",
                        DescribeIdentityError(json) ?? "Try again in a moment.");
            }
        }

        throw new LauncherException(
            "The sign-in code expired before it was used.",
            "Start the sign-in again to get a new code.");
    }

    /// <summary>Runs the Xbox Live, XSTS and Minecraft exchanges and reads the profile.</summary>
    private async Task<MicrosoftSignIn> CompleteAsync(MicrosoftTokens microsoft, CancellationToken cancellationToken)
    {
        var (xblToken, _) = await AuthenticateXboxLiveAsync(microsoft.AccessToken, cancellationToken).ConfigureAwait(false);
        var (xstsToken, userHash) = await AuthorizeXstsAsync(xblToken, cancellationToken).ConfigureAwait(false);
        var minecraft = await LoginWithXboxAsync(userHash, xstsToken, cancellationToken).ConfigureAwait(false);
        var profile = await ReadProfileAsync(minecraft.AccessToken, cancellationToken).ConfigureAwait(false);

        _log.Info(Category, $"Signed in as \"{profile.Name}\".");

        var account = new Account
        {
            Kind = AccountKind.Microsoft,
            Username = profile.Name,
            Uuid = profile.Id,
            TokenExpiresAt = minecraft.ExpiresAt,
        };

        return new MicrosoftSignIn(
            account,
            new AccountTokens(minecraft.AccessToken, microsoft.RefreshToken, minecraft.ExpiresAt));
    }

    private async Task<(string Token, string UserHash)> AuthenticateXboxLiveAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",

                // The d= prefix is what marks this as a Microsoft identity token
                // rather than an Xbox one.
                RpsTicket = "d=" + microsoftAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        using var response = await _http.Client.PostAsJsonAsync(XboxLiveUrl, payload, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(
                "Xbox Live would not accept the sign-in.",
                "This usually clears up on a retry. If it does not, check that the Microsoft account is in good standing.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ReadXboxToken(json);
    }

    private async Task<(string Token, string UserHash)> AuthorizeXstsAsync(
        string xboxLiveToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxLiveToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        using var response = await _http.Client.PostAsJsonAsync(XstsUrl, payload, cancellationToken)
            .ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new LauncherException("Xbox Live refused this account.", DescribeXstsError(json));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(
                "The Xbox Live authorisation step failed.",
                "Try signing in again in a moment.");
        }

        return ReadXboxToken(json);
    }

    private async Task<MinecraftToken> LoginWithXboxAsync(
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };

        using var response = await _http.Client.PostAsJsonAsync(MinecraftLoginUrl, payload, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(
                "Minecraft would not accept the Xbox Live sign-in.",
                "Check that this Microsoft account owns Minecraft: Java Edition.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var token = Text(root, "access_token")
            ?? throw new LauncherException("Minecraft did not return an access token.", "Try signing in again.");

        var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? seconds
            : 86400;

        return new MinecraftToken(token, DateTimeOffset.Now.AddSeconds(expiresIn));
    }

    private async Task<(string Id, string Name)> ReadProfileAsync(
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        using var response = await _http.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new LauncherException(
                "That Microsoft account does not have a Minecraft profile.",
                "The account is signed in, but it does not own Minecraft: Java Edition, or a profile has not been " +
                "created for it yet. An offline account can be used to play on servers that allow it.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LauncherException(
                "The Minecraft profile could not be read.",
                "The account is signed in, but Minecraft's services did not respond. Try again in a moment.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return (
            Text(root, "id") ?? throw new LauncherException("The Minecraft profile had no id.", null),
            Text(root, "name") ?? "Player");
    }

    private static MicrosoftTokens ReadMicrosoftTokens(JsonElement root)
    {
        var access = Text(root, "access_token")
            ?? throw new LauncherException("Microsoft did not return an access token.", "Try signing in again.");

        // Without a refresh token every restart would need a fresh sign-in, so
        // its absence is worth failing on rather than papering over.
        var refresh = Text(root, "refresh_token")
            ?? throw new LauncherException(
                "Microsoft did not return a refresh token.",
                "Check that the application registration requests the offline_access scope.");

        return new MicrosoftTokens(access, refresh);
    }

    private static (string Token, string UserHash) ReadXboxToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var token = Text(root, "Token")
            ?? throw new LauncherException("Xbox Live did not return a token.", "Try signing in again.");

        var userHash = string.Empty;
        if (root.TryGetProperty("DisplayClaims", out var claims)
            && claims.TryGetProperty("xui", out var xui)
            && xui.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in xui.EnumerateArray())
            {
                if (Text(entry, "uhs") is { Length: > 0 } hash)
                {
                    userHash = hash;
                    break;
                }
            }
        }

        return (token, userHash);
    }

    /// <summary>Turns an XSTS refusal code into the reason behind it.</summary>
    private static string DescribeXstsError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var code = document.RootElement.TryGetProperty("XErr", out var xerr) && xerr.TryGetInt64(out var value)
                ? value
                : 0;

            return code switch
            {
                2148916233 => "This Microsoft account has no Xbox profile. Create one at xbox.com, then sign in again.",
                2148916235 => "Xbox Live is not available in this account's country or region.",
                2148916236 or 2148916237 =>
                    "This account needs adult verification before it can use Xbox Live.",
                2148916238 =>
                    "This is a child account. It has to be added to a family by an adult before it can sign in.",
                _ => "Xbox Live refused the sign-in. Check that the account can sign in at xbox.com.",
            };
        }
        catch (JsonException)
        {
            return "Xbox Live refused the sign-in.";
        }
    }

    /// <summary>Pulls the human-readable part out of a Microsoft identity error.</summary>
    private static string? DescribeIdentityError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var description = Text(document.RootElement, "error_description");

            // These responses carry a trace id and timestamp after the first
            // line, which is noise in a dialog.
            return description?.Split('\r', '\n')[0];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record DeviceCodeResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int ExpiresIn,
        int Interval);

    private sealed record MicrosoftTokens(string AccessToken, string RefreshToken);

    private sealed record MinecraftToken(string AccessToken, DateTimeOffset ExpiresAt);
}
