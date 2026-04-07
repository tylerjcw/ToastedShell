namespace Tosh.Core;

public sealed class HttpResponseInfo
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _headers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _contentHeaders;

    public HttpResponseInfo(
        int statusCode,
        string? reasonPhrase,
        bool isSuccess,
        string method,
        Uri? requestUri,
        Uri? finalUri,
        string version,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> contentHeaders,
        string? contentType,
        long? contentLength,
        TimeSpan duration,
        object? body,
        string? savedTo = null)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        IsSuccess = isSuccess;
        Method = method;
        RequestUri = requestUri;
        FinalUri = finalUri;
        Version = version;
        _headers = headers;
        _contentHeaders = contentHeaders;
        ContentType = contentType;
        ContentLength = contentLength;
        Duration = duration;
        Body = body;
        SavedTo = savedTo;
    }

    public int StatusCode { get; }

    public string? ReasonPhrase { get; }

    public bool IsSuccess { get; }

    public string Method { get; }

    public Uri? RequestUri { get; }

    public Uri? FinalUri { get; }

    public string Version { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers => _headers;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ContentHeaders => _contentHeaders;

    public string? ContentType { get; }

    public long? ContentLength { get; }

    public TimeSpan Duration { get; }

    public object? Body { get; }

    public string? SavedTo { get; }

    public string Status => string.IsNullOrWhiteSpace(ReasonPhrase)
        ? StatusCode.ToString()
        : $"{StatusCode} {ReasonPhrase}";

    public override string ToString()
    {
        var target = FinalUri ?? RequestUri;
        return target is null ? Status : $"{Status} {Method} {target}";
    }
}
