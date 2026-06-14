using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChaoticWebtoonReader.Models;

namespace ChaoticWebtoonReader.Services;

public sealed class PatreonAuthService
{
    private const string ClientIdKey = "patreon.client_id";
    private const string RedirectUriKey = "patreon.redirect_uri";
    private const string ClientSecretKey = "patreon.client_secret";
    private const string AccessTokenKey = "patreon.access_token";
    private const string RefreshTokenKey = "patreon.refresh_token";
    private const string TokenTypeKey = "patreon.token_type";
    private const string ExpiresAtKey = "patreon.expires_at";
    private const string AccountCacheKey = "patreon.account_cache";
    private const string AuthorizeEndpoint = "https://www.patreon.com/oauth2/authorize";
    private const string TokenEndpoint = "https://www.patreon.com/api/oauth2/token";
    private const string IdentityEndpoint = "https://www.patreon.com/api/oauth2/v2/identity?include=memberships,memberships.campaign,memberships.currently_entitled_tiers&fields%5Buser%5D=full_name,image_url,url&fields%5Bmember%5D=patron_status,currently_entitled_amount_cents&fields%5Bcampaign%5D=vanity,creation_name,url&fields%5Btier%5D=title,amount_cents";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = new();

    public PatreonAuthService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("JankWebtoonReader/0.1");
    }

    public async Task<PatreonSettingsState> GetSettingsAsync()
    {
        var clientId = Preferences.Default.Get(ClientIdKey, string.Empty);
        var redirectUri = Preferences.Default.Get(RedirectUriKey, GetDefaultRedirectUri());
        var secret = await SecureStorage.Default.GetAsync(ClientSecretKey);

        return new PatreonSettingsState(
            clientId,
            string.IsNullOrWhiteSpace(redirectUri) ? GetDefaultRedirectUri() : redirectUri,
            !string.IsNullOrWhiteSpace(secret),
            GetDefaultRedirectUri());
    }

    public async Task SaveSettingsAsync(string clientId, string clientSecret, string redirectUri)
    {
        clientId = clientId.Trim();
        redirectUri = string.IsNullOrWhiteSpace(redirectUri)
            ? GetDefaultRedirectUri()
            : redirectUri.Trim();

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            throw new PatreonAuthException("Redirect URI must be an absolute URI.");
        }

        Preferences.Default.Set(ClientIdKey, clientId);
        Preferences.Default.Set(RedirectUriKey, redirectUri);

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            await SecureStorage.Default.SetAsync(ClientSecretKey, clientSecret.Trim());
        }
    }

    public async Task<PatreonAccountState> GetAccountStateAsync()
    {
        var settings = await GetSettingsAsync();
        var accessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new PatreonAccountState(settings.IsConfigured(), false, null, null, null, []);
        }

        var cached = ReadCachedAccount();
        if (cached is not null)
        {
            return cached with { IsConfigured = settings.IsConfigured(), IsConnected = true };
        }

        return await RefreshAccountAsync();
    }

    public async Task<PatreonAccountState> ConnectAsync()
    {
        var settings = await GetSettingsAsync();
        var clientSecret = await SecureStorage.Default.GetAsync(ClientSecretKey);

        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new PatreonAuthException("Add a Patreon client ID and client secret first.");
        }

        if (!Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var redirectUri))
        {
            throw new PatreonAuthException("Redirect URI is invalid.");
        }

        var state = CreateState();
        var authorizeUri = BuildAuthorizeUri(settings.ClientId, settings.RedirectUri, state);
        var code = await GetAuthorizationCodeAsync(authorizeUri, redirectUri, state);
        var token = await ExchangeCodeAsync(code, settings, clientSecret);
        await StoreTokenAsync(token);

        return await RefreshAccountAsync();
    }

    public async Task<PatreonAccountState> RefreshAccountAsync()
    {
        var settings = await GetSettingsAsync();
        if (!settings.IsConfigured())
        {
            throw new PatreonAuthException("Add Patreon OAuth settings first.");
        }

        await EnsureFreshTokenAsync(settings);

        var accessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new PatreonAuthException("Connect Patreon first.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, IdentityEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new PatreonAuthException($"Patreon account refresh failed: {DescribePatreonError(content)}");
        }

        var expiresAtValue = await SecureStorage.Default.GetAsync(ExpiresAtKey);
        DateTimeOffset.TryParse(expiresAtValue, out var expiresAt);
        var account = ParseAccount(content, settings, expiresAt == default ? null : expiresAt);
        Preferences.Default.Set(AccountCacheKey, JsonSerializer.Serialize(account, JsonOptions));
        return account;
    }

    public async Task DisconnectAsync()
    {
        SecureStorage.Default.Remove(AccessTokenKey);
        SecureStorage.Default.Remove(RefreshTokenKey);
        SecureStorage.Default.Remove(TokenTypeKey);
        SecureStorage.Default.Remove(ExpiresAtKey);
        Preferences.Default.Remove(AccountCacheKey);
        await Task.CompletedTask;
    }

    public bool HasRequiredTier(PatreonAccountState account, string campaignId, IReadOnlySet<string> tierIds)
    {
        return account.Memberships.Any(membership =>
            string.Equals(membership.CampaignId, campaignId, StringComparison.Ordinal)
            && string.Equals(membership.PatronStatus, "active_patron", StringComparison.OrdinalIgnoreCase)
            && membership.Tiers.Any(tier => tierIds.Contains(tier.Id)));
    }

    private async Task EnsureFreshTokenAsync(PatreonSettingsState settings)
    {
        var expiresAtValue = await SecureStorage.Default.GetAsync(ExpiresAtKey);
        if (!DateTimeOffset.TryParse(expiresAtValue, out var expiresAt)
            || expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return;
        }

        var refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
        var clientSecret = await SecureStorage.Default.GetAsync(ClientSecretKey);

        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = settings.ClientId,
            ["client_secret"] = clientSecret
        };

        var token = await PostTokenAsync(values);
        await StoreTokenAsync(token);
    }

    private async Task<string> GetAuthorizationCodeAsync(Uri authorizeUri, Uri redirectUri, string state)
    {
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI && IsLoopbackRedirect(redirectUri))
        {
            return await GetLoopbackAuthorizationCodeAsync(authorizeUri, redirectUri, state);
        }

        var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = authorizeUri,
            CallbackUrl = redirectUri,
            PrefersEphemeralWebBrowserSession = false
        });

        if (!result.Properties.TryGetValue("state", out var returnedState)
            || !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new PatreonAuthException("Patreon login returned an invalid state.");
        }

        if (result.Properties.TryGetValue("error", out var error))
        {
            throw new PatreonAuthException($"Patreon login failed: {error}");
        }

        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new PatreonAuthException("Patreon did not return an authorization code.");
        }

        return code;
    }

    private async Task<string> GetLoopbackAuthorizationCodeAsync(Uri authorizeUri, Uri redirectUri, string state)
    {
        if (redirectUri.Port <= 0)
        {
            throw new PatreonAuthException("Windows loopback redirect URI must include a port.");
        }

        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        listener.Start();

        try
        {
            await Browser.Default.OpenAsync(authorizeUri, BrowserLaunchMode.External);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var requestTarget = await ReadRequestTargetAsync(client, timeout.Token);
            var callbackUri = new Uri($"{redirectUri.Scheme}://{redirectUri.Host}:{redirectUri.Port}{requestTarget}");
            var values = ParseQuery(callbackUri.Query);

            await WriteLoopbackResponseAsync(client);

            if (!values.TryGetValue("state", out var returnedState)
                || !string.Equals(returnedState, state, StringComparison.Ordinal))
            {
                throw new PatreonAuthException("Patreon login returned an invalid state.");
            }

            if (values.TryGetValue("error", out var error))
            {
                throw new PatreonAuthException($"Patreon login failed: {error}");
            }

            if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                throw new PatreonAuthException("Patreon did not return an authorization code.");
            }

            return code;
        }
        catch (OperationCanceledException)
        {
            throw new PatreonAuthException("Timed out waiting for Patreon login.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<PatreonTokenResponse> ExchangeCodeAsync(
        string code,
        PatreonSettingsState settings,
        string clientSecret)
    {
        var values = new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = settings.RedirectUri
        };

        return await PostTokenAsync(values);
    }

    private async Task<PatreonTokenResponse> PostTokenAsync(Dictionary<string, string> values)
    {
        using var response = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(values));
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new PatreonAuthException($"Patreon token request failed: {DescribePatreonError(content)}");
        }

        return JsonSerializer.Deserialize<PatreonTokenResponse>(content, JsonOptions)
            ?? throw new PatreonAuthException("Patreon returned an empty token response.");
    }

    private static async Task StoreTokenAsync(PatreonTokenResponse token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new PatreonAuthException("Patreon did not return an access token.");
        }

        await SecureStorage.Default.SetAsync(AccessTokenKey, token.AccessToken);

        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            await SecureStorage.Default.SetAsync(RefreshTokenKey, token.RefreshToken);
        }

        if (!string.IsNullOrWhiteSpace(token.TokenType))
        {
            await SecureStorage.Default.SetAsync(TokenTypeKey, token.TokenType);
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(token.ExpiresIn, 0));
        await SecureStorage.Default.SetAsync(ExpiresAtKey, expiresAt.ToString("O"));
    }

    private PatreonAccountState ParseAccount(string content, PatreonSettingsState settings, DateTimeOffset? expiresAt)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var data = root.GetProperty("data");
        var userId = data.GetProperty("id").GetString();
        var displayName = GetAttributeString(data, "full_name") ?? "Patreon member";
        var campaigns = new Dictionary<string, string>();
        var tiers = new Dictionary<string, string>();
        var memberships = new List<PatreonMembership>();

        if (root.TryGetProperty("included", out var included) && included.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in included.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                var id = item.GetProperty("id").GetString() ?? string.Empty;

                if (type == "campaign")
                {
                    campaigns[id] =
                        GetAttributeString(item, "creation_name")
                        ?? GetAttributeString(item, "vanity")
                        ?? $"Campaign {id}";
                }
                else if (type == "tier")
                {
                    tiers[id] = GetAttributeString(item, "title") ?? $"Tier {id}";
                }
            }

            foreach (var item in included.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "member")
                {
                    continue;
                }

                var memberId = item.GetProperty("id").GetString() ?? string.Empty;
                var campaignId = GetRelationshipId(item, "campaign");
                var tierIds = GetRelationshipIds(item, "currently_entitled_tiers");
                var membershipTiers = tierIds
                    .Select(id => new PatreonTier(id, tiers.TryGetValue(id, out var title) ? title : $"Tier {id}"))
                    .ToList();

                memberships.Add(new PatreonMembership(
                    memberId,
                    campaignId,
                    campaignId is not null && campaigns.TryGetValue(campaignId, out var campaignName)
                        ? campaignName
                        : "Patreon campaign",
                    GetAttributeString(item, "patron_status"),
                    GetAttributeInt(item, "currently_entitled_amount_cents"),
                    membershipTiers));
            }
        }

        return new PatreonAccountState(
            settings.IsConfigured(),
            true,
            userId,
            displayName,
            expiresAt,
            memberships.OrderBy(membership => membership.CampaignName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private PatreonAccountState? ReadCachedAccount()
    {
        var value = Preferences.Default.Get(AccountCacheKey, string.Empty);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<PatreonAccountState>(value, JsonOptions);
    }

    private static Uri BuildAuthorizeUri(string clientId, string redirectUri, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "identity identity.memberships",
            ["state"] = state
        };

        var encodedQuery = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new Uri($"{AuthorizeEndpoint}?{encodedQuery}");
    }

    private static string CreateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsLoopbackRedirect(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDefaultRedirectUri()
    {
        return DeviceInfo.Current.Platform == DevicePlatform.WinUI
            ? "http://127.0.0.1:52891/patreon-callback"
            : "jankwebtoonreader://patreon-auth";
    }

    private static async Task<string> ReadRequestTargetAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            throw new PatreonAuthException("Patreon login callback was empty.");
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new PatreonAuthException("Patreon login callback was invalid.");
        }

        return parts[1];
    }

    private static async Task WriteLoopbackResponseAsync(TcpClient client)
    {
        const string body = "<!doctype html><html><body><h1>Patreon connected</h1><p>You can close this window and return to Jank Webtoon Reader.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" +
            body);

        await client.GetStream().WriteAsync(bytes);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            var key = WebUtility.UrlDecode(split[0]);
            var value = split.Length > 1 ? WebUtility.UrlDecode(split[1]) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private static string? GetRelationshipId(JsonElement item, string relationshipName)
    {
        if (!item.TryGetProperty("relationships", out var relationships)
            || !relationships.TryGetProperty(relationshipName, out var relationship)
            || !relationship.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return data.GetProperty("id").GetString();
    }

    private static IReadOnlyList<string> GetRelationshipIds(JsonElement item, string relationshipName)
    {
        if (!item.TryGetProperty("relationships", out var relationships)
            || !relationships.TryGetProperty(relationshipName, out var relationship)
            || !relationship.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    private static string? GetAttributeString(JsonElement item, string name)
    {
        return item.TryGetProperty("attributes", out var attributes)
            && attributes.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static int? GetAttributeInt(JsonElement item, string name)
    {
        return item.TryGetProperty("attributes", out var attributes)
            && attributes.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
                ? result
                : null;
    }

    private static string? GetTopLevelString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string DescribePatreonError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "empty response";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors.EnumerateArray()
                    .Select(error => GetTopLevelString(error, "detail") ?? GetTopLevelString(error, "title"))
                    .Where(message => !string.IsNullOrWhiteSpace(message));

                return string.Join("; ", messages);
            }
        }
        catch (JsonException)
        {
        }

        return content.Length > 240 ? $"{content[..240]}..." : content;
    }

    private sealed record PatreonTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

internal static class PatreonSettingsStateExtensions
{
    public static bool IsConfigured(this PatreonSettingsState settings)
    {
        return !string.IsNullOrWhiteSpace(settings.ClientId)
            && settings.HasClientSecret
            && !string.IsNullOrWhiteSpace(settings.RedirectUri);
    }
}
