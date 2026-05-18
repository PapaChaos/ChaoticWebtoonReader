namespace ChaoticWebtoonReader.Models;

public sealed record PatreonMembership(
    string MemberId,
    string? CampaignId,
    string CampaignName,
    string? PatronStatus,
    int? CurrentlyEntitledAmountCents,
    IReadOnlyList<PatreonTier> Tiers);
