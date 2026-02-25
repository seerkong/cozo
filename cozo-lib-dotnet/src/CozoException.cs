namespace Cozo.DotNet;

public sealed class CozoException : Exception
{
    public CozoException(string message, string? rawResponse = null, Exception? innerException = null)
        : base(message, innerException)
    {
        RawResponse = rawResponse;
    }

    public string? RawResponse { get; }
}
