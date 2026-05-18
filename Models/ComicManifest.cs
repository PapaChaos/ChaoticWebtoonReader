namespace ChaoticWebtoonReader.Models;

public sealed record ComicManifest(
    string Title,
    string SourceType,
    string SourcePath,
    int PageCount,
    IReadOnlyList<ComicPage> Pages);
