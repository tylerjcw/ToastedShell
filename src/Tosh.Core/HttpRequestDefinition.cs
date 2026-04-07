namespace Tosh.Core;

public sealed class HttpRequestDefinition
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _headers;
    private readonly byte[]? _bodyBytes;

    public HttpRequestDefinition(
        string method,
        Uri requestUri,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        byte[]? bodyBytes = null,
        string? contentType = null,
        TimeSpan? timeout = null,
        bool followRedirects = true,
        string? bodyKind = null,
        string? bodyPreview = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(headers);

        Method = method.ToUpperInvariant();
        RequestUri = requestUri;
        _headers = headers;
        _bodyBytes = bodyBytes;
        ContentType = contentType;
        Timeout = timeout;
        FollowRedirects = followRedirects;
        BodyKind = bodyKind;
        BodyPreview = bodyPreview;
    }

    public string Method { get; }

    public Uri RequestUri { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers => _headers;

    public string? ContentType { get; }

    public long? ContentLength => _bodyBytes?.LongLength;

    public TimeSpan? Timeout { get; }

    public bool FollowRedirects { get; }

    public string? BodyKind { get; }

    public string? BodyPreview { get; }

    internal byte[]? BodyBytes => _bodyBytes;

    public override string ToString()
    {
        return $"{Method} {RequestUri}";
    }
}
