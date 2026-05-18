namespace ChaoticWebtoonReader.Services;

public sealed class ReaderSourceException : Exception
{
    public ReaderSourceException(string message)
        : base(message)
    {
    }
}
