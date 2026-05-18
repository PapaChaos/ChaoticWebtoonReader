namespace ChaoticWebtoonReader.Models;

public sealed record PatreonSettingsState(
    string ClientId,
    string RedirectUri,
    bool HasClientSecret,
    string DefaultRedirectUri);
