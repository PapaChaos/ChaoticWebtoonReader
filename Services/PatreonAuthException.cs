namespace ChaoticWebtoonReader.Services;

public sealed class PatreonAuthException : Exception
{
    public PatreonAuthException(string message)
        : base(message)
    {
    }
}
