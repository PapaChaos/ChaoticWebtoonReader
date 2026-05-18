namespace ChaoticWebtoonReader.Models;

public sealed record ComicPage(
    int Index,
    string Name,
    string Location,
    long Size,
    string Extension);
