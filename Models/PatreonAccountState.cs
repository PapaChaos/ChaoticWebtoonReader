namespace ChaoticWebtoonReader.Models;

public sealed record PatreonAccountState(
    bool IsConfigured,
    bool IsConnected,
    string? UserId,
    string? DisplayName,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<PatreonMembership> Memberships);
