using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Tosh.Stdlib.Net;

public sealed class HttpFileServerHandle : IDisposable, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<int, HttpFileServerHandle> OpenHandles = new();
    private static int _nextId;

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _backgroundTask;
    private readonly string _rootPath;
    private readonly string _bindAddress;
    private readonly bool _directoryBrowsingEnabled;
    private readonly bool _uploadEnabled;
    private readonly bool _serveOnce;
    private readonly string _indexFileName;
    private readonly string? _accessToken;
    private readonly IReadOnlyList<string> _shareHosts;

    private int _closed;
    private long _requestCount;

    private HttpFileServerHandle(
        HttpListener listener,
        string rootPath,
        string bindAddress,
        Uri url,
        int port,
        bool directoryBrowsingEnabled,
        bool uploadEnabled,
        bool serveOnce,
        string indexFileName,
        string? accessToken,
        IReadOnlyList<string> shareHosts)
    {
        Id = Interlocked.Increment(ref _nextId);
        _listener = listener;
        _rootPath = rootPath;
        _bindAddress = bindAddress;
        Url = url;
        Port = port;
        _directoryBrowsingEnabled = directoryBrowsingEnabled;
        _uploadEnabled = uploadEnabled;
        _serveOnce = serveOnce;
        _indexFileName = indexFileName;
        _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
        _shareHosts = shareHosts.Count == 0 ? [url.Host] : shareHosts;
        StartedAt = DateTimeOffset.Now;

        OpenHandles[Id] = this;
        _backgroundTask = Task.Run(RunAsync);
    }

    public int Id { get; }

    public string RootPath => _rootPath;

    public string BindAddress => _bindAddress;

    public Uri Url { get; }

    public Uri ShareUrl => BuildAbsoluteShareUri("/");

    public IReadOnlyList<Uri> ShareUrls => BuildAbsoluteShareUris("/");

    public int Port { get; }

    public bool DirectoryBrowsingEnabled => _directoryBrowsingEnabled;

    public bool UploadEnabled => _uploadEnabled;

    public bool ServeOnce => _serveOnce;

    public string IndexFileName => _indexFileName;

    public string? AccessToken => _accessToken;

    public bool RequiresToken => _accessToken is not null;

    public DateTimeOffset StartedAt { get; }

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public bool IsOpen => Volatile.Read(ref _closed) == 0 && _listener.IsListening;

    public static HttpFileServerHandle Start(
        string rootPath,
        string? bindAddress = null,
        int port = 0,
        bool directoryBrowsingEnabled = false,
        bool uploadEnabled = false,
        bool serveOnce = false,
        string indexFileName = "index.html",
        string? accessToken = null)
    {
        var fullRootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(fullRootPath))
        {
            throw new InvalidOperationException($"Directory '{fullRootPath}' does not exist.");
        }

        if (string.IsNullOrWhiteSpace(indexFileName))
        {
            indexFileName = "index.html";
        }

        var requestedBindAddress = string.IsNullOrWhiteSpace(bindAddress)
            ? "127.0.0.1"
            : bindAddress.Trim();

        var effectivePort = port == 0 ? AllocatePort(requestedBindAddress) : port;

        if (effectivePort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535, or 0 to request a random available port.");
        }

        var prefixHost = NormalizePrefixHost(requestedBindAddress);
        var displayHost = NormalizeDisplayHost(requestedBindAddress);
        var shareHosts = ResolveShareHosts(requestedBindAddress);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{prefixHost}:{effectivePort}/");

        try
        {
            listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not start HTTP file server on {requestedBindAddress}:{effectivePort}. {exception.Message}",
                exception);
        }

        return new HttpFileServerHandle(
            listener,
            fullRootPath,
            requestedBindAddress,
            new Uri($"http://{NormalizeUriHost(displayHost)}:{effectivePort}/"),
            effectivePort,
            directoryBrowsingEnabled,
            uploadEnabled,
            serveOnce,
            indexFileName,
            accessToken,
            shareHosts);
    }

    public static IReadOnlyList<HttpFileServerHandle> GetOpenHandles()
    {
        return OpenHandles.Values
            .Where(handle => handle.IsOpen)
            .OrderBy(handle => handle.Id)
            .ToArray();
    }

    public static bool TryGetOpenHandle(int id, out HttpFileServerHandle handle)
    {
        if (OpenHandles.TryGetValue(id, out var existing) && existing.IsOpen)
        {
            handle = existing;
            return true;
        }

        handle = null!;
        return false;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        OpenHandles.TryRemove(Id, out _);
        _cancellation.Cancel();

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch
        {
        }

        try
        {
            _listener.Close();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Close();

        try
        {
            _backgroundTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Close();

        try
        {
            await _backgroundTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _cancellation.Dispose();
    }

    public override string ToString()
    {
        return $"{Url} -> {RootPath}";
    }

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (_serveOnce)
            {
                await HandleRequestSafeAsync(context);
                Close();
                break;
            }

            _ = Task.Run(() => HandleRequestSafeAsync(context));
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestAsync(context);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                var payload = Encoding.UTF8.GetBytes("Internal Server Error");
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload);
            }
            catch
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (!IsAuthorized(context.Request))
        {
            await WriteUnauthorizedResponseAsync(context);
            return;
        }

        Interlocked.Increment(ref _requestCount);

        switch (context.Request.HttpMethod.ToUpperInvariant())
        {
            case "GET":
            case "HEAD":
                await HandleDownloadAsync(context);
                return;

            case "PUT":
            case "POST":
                if (!_uploadEnabled)
                {
                    await WriteTextResponseAsync(context, 405, "Uploads are disabled for this server.");
                    return;
                }

                await HandleUploadAsync(context);
                return;

            default:
                context.Response.Headers["Allow"] = _uploadEnabled ? "GET, HEAD, PUT, POST" : "GET, HEAD";
                await WriteTextResponseAsync(context, 405, $"HTTP method '{context.Request.HttpMethod}' is not supported.");
                return;
        }
    }

    private async Task HandleDownloadAsync(HttpListenerContext context)
    {
        var targetPath = ResolveRequestPath(context.Request.Url);

        if (targetPath is null)
        {
            await WriteTextResponseAsync(context, 403, "Forbidden");
            return;
        }

        if (Directory.Exists(targetPath))
        {
            if (context.Request.Url is not null &&
                !context.Request.Url.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 301;
                context.Response.RedirectLocation = AppendTokenQuery($"{context.Request.Url.AbsolutePath}/");
                context.Response.Close();
                return;
            }

            var indexPath = Path.Combine(targetPath, _indexFileName);

            if (File.Exists(indexPath))
            {
                await WriteFileResponseAsync(context, indexPath);
                return;
            }

            if (_directoryBrowsingEnabled || _uploadEnabled)
            {
                var html = BuildDirectoryListing(context.Request.Url?.AbsolutePath ?? "/", targetPath);
                await WriteTextResponseAsync(context, 200, html, "text/html; charset=utf-8");
                return;
            }

            await WriteTextResponseAsync(context, 404, "Not Found");
            return;
        }

        if (!File.Exists(targetPath))
        {
            await WriteTextResponseAsync(context, 404, "Not Found");
            return;
        }

        await WriteFileResponseAsync(context, targetPath);
    }

    private async Task HandleUploadAsync(HttpListenerContext context)
    {
        var targetPath = ResolveRequestPath(context.Request.Url);

        if (targetPath is null)
        {
            await WriteTextResponseAsync(context, 403, "Forbidden");
            return;
        }

        var contentType = context.Request.ContentType ?? string.Empty;

        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMultipartUploadAsync(context, targetPath, contentType);
            return;
        }

        if (context.Request.Url is not null &&
            context.Request.Url.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            await WriteTextResponseAsync(context, 400, "Uploads require a file path, not a directory path.");
            return;
        }

        var fileName = Path.GetFileName(targetPath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            await WriteTextResponseAsync(context, 400, "Uploads require a file path.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? RootPath);
        var existed = File.Exists(targetPath);

        await using (var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await context.Request.InputStream.CopyToAsync(stream);
        }

        var statusCode = existed ? 200 : 201;
        await WriteTextResponseAsync(context, statusCode, targetPath, "text/plain; charset=utf-8");
    }

    private async Task HandleMultipartUploadAsync(HttpListenerContext context, string targetPath, string contentType)
    {
        var targetDirectory = Directory.Exists(targetPath)
            ? targetPath
            : context.Request.Url is not null && context.Request.Url.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                ? targetPath
                : null;

        if (targetDirectory is null)
        {
            await WriteTextResponseAsync(context, 400, "Multipart uploads must target a directory URL.");
            return;
        }

        var boundary = ExtractMultipartBoundary(contentType);
        var files = await ParseMultipartFilesAsync(context.Request.InputStream, boundary);

        if (files.Count == 0)
        {
            await WriteTextResponseAsync(context, 400, "No file parts were found in the upload.");
            return;
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var file in files)
        {
            var safeName = Path.GetFileName(file.FileName);

            if (string.IsNullOrWhiteSpace(safeName))
            {
                continue;
            }

            var fullPath = Path.Combine(targetDirectory, safeName);
            await File.WriteAllBytesAsync(fullPath, file.Content);
        }

        context.Response.StatusCode = 303;
        context.Response.RedirectLocation = AppendTokenQuery(context.Request.Url?.AbsolutePath ?? "/");
        context.Response.Close();
    }

    private async Task WriteFileResponseAsync(HttpListenerContext context, string path)
    {
        var info = new FileInfo(path);
        context.Response.StatusCode = 200;
        context.Response.ContentType = GuessContentType(path);
        context.Response.ContentLength64 = info.Length;

        if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Close();
            return;
        }

        await using var stream = File.OpenRead(path);
        await stream.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }

    private async Task WriteUnauthorizedResponseAsync(HttpListenerContext context)
    {
        context.Response.Headers["WWW-Authenticate"] = "Bearer";

        if (_directoryBrowsingEnabled || _uploadEnabled)
        {
            var html = BuildUnauthorizedPage();
            await WriteTextResponseAsync(context, 401, html, "text/html; charset=utf-8");
            return;
        }

        await WriteTextResponseAsync(context, 401, "Unauthorized");
    }

    private static async Task WriteTextResponseAsync(
        HttpListenerContext context,
        int statusCode,
        string text,
        string contentType = "text/plain; charset=utf-8")
    {
        var payload = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = payload.Length;

        if (!string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await context.Response.OutputStream.WriteAsync(payload);
        }

        context.Response.Close();
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (_accessToken is null)
        {
            return true;
        }

        var queryToken = request.QueryString["token"];

        if (TokensMatch(queryToken, _accessToken))
        {
            return true;
        }

        var authHeader = request.Headers["Authorization"];

        if (!string.IsNullOrWhiteSpace(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearerToken = authHeader["Bearer ".Length..].Trim();
            return TokensMatch(bearerToken, _accessToken);
        }

        return false;
    }

    private string? ResolveRequestPath(Uri? url)
    {
        var absolutePath = url?.AbsolutePath ?? "/";
        var decodedPath = Uri.UnescapeDataString(absolutePath);
        var relativePath = decodedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var candidate = string.IsNullOrWhiteSpace(relativePath)
            ? RootPath
            : Path.GetFullPath(Path.Combine(RootPath, relativePath));

        return IsPathUnderRoot(candidate) ? candidate : null;
    }

    private bool IsPathUnderRoot(string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedRoot, normalizedCandidate, comparison) ||
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private string BuildDirectoryListing(string requestPath, string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        var entries = directory.EnumerateFileSystemInfos()
            .OrderByDescending(entry => entry is DirectoryInfo)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentShareUrls = BuildAbsoluteShareUris(requestPath);
        var currentShareUrl = currentShareUrls[0];
        var currentDirectoryLabel = string.IsNullOrWhiteSpace(requestPath) ? "/" : requestPath;

        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Index of ");
        builder.Append(WebUtility.HtmlEncode(requestPath));
        builder.Append("</title><style>");
        builder.Append("body{font-family:ui-sans-serif,system-ui,sans-serif;max-width:1080px;margin:2rem auto;padding:0 1rem;color:#111;}h1{margin-bottom:.35rem;}table{width:100%;border-collapse:collapse;margin-top:1rem;}th,td{text-align:left;padding:.45rem .55rem;border-bottom:1px solid #ddd;vertical-align:top;}th{font-size:.9rem;color:#444;}code{background:#f4f4f4;padding:.15rem .35rem;border-radius:.25rem;}form{margin:1rem 0;padding:1rem;border:1px solid #ddd;border-radius:.5rem;background:#fafafa;}input[type=file]{margin-right:.75rem;}a{text-decoration:none;}a:hover{text-decoration:underline;}.muted{color:#666;font-size:.95rem;}.path{font-family:ui-monospace,monospace;}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:.85rem;margin:1rem 0;}.card{border:1px solid #ddd;border-radius:.6rem;padding:.85rem;background:#fcfcfc;}.card strong{display:block;margin-bottom:.35rem;}ul{margin:.25rem 0 0 1.1rem;padding:0;}");
        builder.Append("</style></head><body>");
        builder.Append("<h1>Index of ");
        builder.Append(WebUtility.HtmlEncode(currentDirectoryLabel));
        builder.Append("</h1>");
        builder.Append("<div class=\"muted\">Temporary ToSh file server</div>");
        builder.Append("<div class=\"grid\">");
        builder.Append("<div class=\"card\"><strong>Share URL</strong><a class=\"path\" href=\"");
        builder.Append(WebUtility.HtmlEncode(currentShareUrl.ToString()));
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(currentShareUrl.ToString()));
        builder.Append("</a></div>");
        if (!Uri.Compare(Url, currentShareUrl, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase).Equals(0))
        {
            builder.Append("<div class=\"card\"><strong>Local URL</strong><a class=\"path\" href=\"");
            builder.Append(WebUtility.HtmlEncode(BuildAbsoluteLocalUri(requestPath).ToString()));
            builder.Append("\">");
            builder.Append(WebUtility.HtmlEncode(BuildAbsoluteLocalUri(requestPath).ToString()));
            builder.Append("</a><div class=\"muted\">Use this on the same machine. Use the share URL from another device.</div></div>");
        }
        builder.Append("<div class=\"card\"><strong>Server</strong><div>id <code>");
        builder.Append(Id.ToString(CultureInfo.InvariantCulture));
        builder.Append("</code>  •  requests <code>");
        builder.Append(RequestCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("</code></div><div class=\"muted\">Root: <span class=\"path\">");
        builder.Append(WebUtility.HtmlEncode(RootPath));
        builder.Append("</span></div></div>");
        builder.Append("<div class=\"card\"><strong>Capabilities</strong><ul><li>Downloads: enabled</li><li>Directory view: ");
        builder.Append(_directoryBrowsingEnabled || _uploadEnabled ? "enabled" : "disabled");
        builder.Append("</li><li>Uploads: ");
        builder.Append(_uploadEnabled ? "enabled" : "disabled");
        builder.Append("</li></ul></div>");

        if (_accessToken is not null)
        {
            builder.Append("<div class=\"card\"><strong>Access token</strong><div><code>");
            builder.Append(WebUtility.HtmlEncode(_accessToken));
            builder.Append("</code></div><div class=\"muted\">Requests may use the share URL or <code>Authorization: Bearer ...</code>. Hyphens and casing are ignored when typing the token.</div></div>");
        }

        if (currentShareUrls.Count > 1)
        {
            builder.Append("<div class=\"card\"><strong>Alternate share URLs</strong><ul>");
            foreach (var alternateUrl in currentShareUrls.Skip(1))
            {
                builder.Append("<li><a class=\"path\" href=\"");
                builder.Append(WebUtility.HtmlEncode(alternateUrl.ToString()));
                builder.Append("\">");
                builder.Append(WebUtility.HtmlEncode(alternateUrl.ToString()));
                builder.Append("</a></li>");
            }
            builder.Append("</ul></div>");
        }

        builder.Append("</div>");

        if (_uploadEnabled)
        {
            builder.Append("<form method=\"post\" enctype=\"multipart/form-data\" action=\"");
            builder.Append(WebUtility.HtmlEncode(AppendTokenQuery(requestPath)));
            builder.Append("\"><strong>Upload files</strong><br><span class=\"muted\">Choose one or more files to upload into this directory, or use PUT/POST from another shell.</span><div style=\"margin-top:.75rem;\"><input type=\"file\" name=\"file\" multiple><button type=\"submit\">Upload</button></div></form>");
            builder.Append("<div class=\"muted\">CLI upload example: <code>curl -T ./file.txt ");
            builder.Append(WebUtility.HtmlEncode(BuildAbsoluteShareUri(requestPath.TrimEnd('/') + "/file.txt").ToString()));
            builder.Append("</code></div>");
        }
        else
        {
            builder.Append("<div class=\"muted\">CLI download example: <code>http get ");
            builder.Append(WebUtility.HtmlEncode(currentShareUrl.ToString()));
            builder.Append(" --as bytes --out ./download.bin</code></div>");
        }

        builder.Append("<table><thead><tr><th>Name</th><th>Type</th><th>Size</th><th>Modified</th></tr></thead><tbody>");

        if (!string.Equals(requestPath, "/", StringComparison.Ordinal))
        {
            builder.Append("<tr><td><a href=\"../");
            if (_accessToken is not null)
            {
                builder.Append("?token=");
                builder.Append(WebUtility.HtmlEncode(Uri.EscapeDataString(_accessToken)));
            }
            builder.Append("\">../</a></td><td>parent</td><td></td><td></td></tr>");
        }

        foreach (var entry in entries)
        {
            var isDirectory = entry is DirectoryInfo;
            var label = isDirectory ? entry.Name + "/" : entry.Name;
            var href = AppendTokenQuery(Uri.EscapeDataString(entry.Name) + (isDirectory ? "/" : string.Empty));
            var size = isDirectory ? string.Empty : FormatFileSize(((FileInfo)entry).Length);
            var modified = entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            builder.Append("<tr><td><a ");
            if (!isDirectory)
            {
                builder.Append("download ");
            }
            builder.Append("href=\"");
            builder.Append(WebUtility.HtmlEncode(href));
            builder.Append("\">");
            builder.Append(WebUtility.HtmlEncode(label));
            builder.Append("</a></td><td>");
            builder.Append(isDirectory ? "directory" : GuessContentType(entry.FullName));
            builder.Append("</td><td>");
            builder.Append(WebUtility.HtmlEncode(size));
            builder.Append("</td><td>");
            builder.Append(WebUtility.HtmlEncode(modified));
            builder.Append("</td></tr>");
        }

        builder.Append("</tbody></table></body></html>");
        return builder.ToString();
    }

    private string BuildUnauthorizedPage()
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Authorization Required</title><style>body{font-family:ui-sans-serif,system-ui,sans-serif;max-width:720px;margin:2rem auto;padding:0 1rem;color:#111;}code{background:#f4f4f4;padding:.15rem .35rem;border-radius:.25rem;}</style></head><body>");
        builder.Append("<h1>Authorization required</h1><p>This temporary file server requires a token.</p><p>Use the share URL printed by ToSh, or send <code>Authorization: Bearer ...</code>.</p></body></html>");
        return builder.ToString();
    }

    private string AppendTokenQuery(string path)
    {
        if (_accessToken is null)
        {
            return path;
        }

        return path.Contains('?', StringComparison.Ordinal)
            ? $"{path}&token={Uri.EscapeDataString(_accessToken)}"
            : $"{path}?token={Uri.EscapeDataString(_accessToken)}";
    }

    private Uri BuildAbsoluteShareUri(string path)
    {
        return BuildAbsoluteShareUris(path)[0];
    }

    private IReadOnlyList<Uri> BuildAbsoluteShareUris(string path)
    {
        return _shareHosts
            .Select(host => BuildAbsoluteUri(host, path))
            .ToArray();
    }

    private Uri BuildAbsoluteLocalUri(string path)
    {
        return BuildAbsoluteUri(Url.Host, path);
    }

    private Uri BuildAbsoluteUri(string host, string path)
    {
        var relativePath = path == "/"
            ? string.Empty
            : path.TrimStart('/');
        var pathAndQuery = AppendTokenQuery(relativePath);
        return new Uri($"http://{NormalizeUriHost(host)}:{Port}/" + pathAndQuery);
    }

    private static async Task<IReadOnlyList<MultipartFileUpload>> ParseMultipartFilesAsync(Stream stream, string boundary)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var data = buffer.ToArray();

        var boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
        var uploads = new List<MultipartFileUpload>();
        var position = 0;

        while (true)
        {
            var boundaryStart = IndexOf(data, boundaryBytes, position);

            if (boundaryStart < 0)
            {
                break;
            }

            var boundaryEnd = boundaryStart + boundaryBytes.Length;

            if (boundaryEnd + 1 < data.Length && data[boundaryEnd] == (byte)'-' && data[boundaryEnd + 1] == (byte)'-')
            {
                break;
            }

            var partStart = boundaryEnd;

            if (partStart + 1 < data.Length && data[partStart] == (byte)'\r' && data[partStart + 1] == (byte)'\n')
            {
                partStart += 2;
            }

            var nextBoundary = IndexOf(data, boundaryBytes, partStart);

            if (nextBoundary < 0)
            {
                break;
            }

            var partEnd = nextBoundary;

            if (partEnd >= 2 && data[partEnd - 2] == (byte)'\r' && data[partEnd - 1] == (byte)'\n')
            {
                partEnd -= 2;
            }

            var headerEnd = IndexOf(data, "\r\n\r\n"u8.ToArray(), partStart, partEnd);

            if (headerEnd < 0)
            {
                position = nextBoundary + boundaryBytes.Length;
                continue;
            }

            var headerText = Encoding.UTF8.GetString(data, partStart, headerEnd - partStart);
            var contentStart = headerEnd + 4;
            var contentLength = Math.Max(0, partEnd - contentStart);
            var contentBytes = new byte[contentLength];
            Array.Copy(data, contentStart, contentBytes, 0, contentLength);

            var fileName = ExtractMultipartFileName(headerText);

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                uploads.Add(new MultipartFileUpload(fileName, contentBytes));
            }

            position = nextBoundary;
        }

        return uploads;
    }

    private static string ExtractMultipartBoundary(string contentType)
    {
        const string marker = "boundary=";
        var index = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            throw new InvalidOperationException("Multipart upload is missing a boundary.");
        }

        var boundary = contentType[(index + marker.Length)..].Trim();
        return boundary.Trim('"');
    }

    private static string? ExtractMultipartFileName(string headerText)
    {
        foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameters = line["Content-Disposition:".Length..]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var parameter in parameters)
            {
                if (parameter.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
                {
                    var encodedValue = parameter["filename*=".Length..].Trim().Trim('"');
                    var delimiterIndex = encodedValue.IndexOf("''", StringComparison.Ordinal);
                    var fileName = delimiterIndex >= 0
                        ? encodedValue[(delimiterIndex + 2)..]
                        : encodedValue;
                    return Uri.UnescapeDataString(fileName);
                }

                if (parameter.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parameter["filename=".Length..].Trim().Trim('"'));
                }
            }

            return null;
        }

        return null;
    }

    private static int IndexOf(byte[] source, byte[] value, int startIndex = 0, int? endExclusive = null)
    {
        var limit = (endExclusive ?? source.Length) - value.Length;

        for (var index = startIndex; index <= limit; index++)
        {
            var matched = true;

            for (var offset = 0; offset < value.Length; offset++)
            {
                if (source[index + offset] != value[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    private static int AllocatePort(string bindAddress)
    {
        var ipAddress = ParseBindAddress(bindAddress);
        using var listener = new TcpListener(ipAddress, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress ParseBindAddress(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var parsed))
        {
            return parsed;
        }

        return bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Loopback;
    }

    private static string NormalizePrefixHost(string bindAddress)
    {
        return bindAddress switch
        {
            "0.0.0.0" or "::" or "*" or "+" => "+",
            _ => NormalizeUriHost(bindAddress),
        };
    }

    private static string NormalizeDisplayHost(string bindAddress)
    {
        return bindAddress switch
        {
            "0.0.0.0" or "::" or "*" or "+" => "localhost",
            _ => bindAddress,
        };
    }

    private static IReadOnlyList<string> ResolveShareHosts(string bindAddress)
    {
        return bindAddress switch
        {
            "0.0.0.0" or "::" or "*" or "+" => ResolveLanHosts(),
            "127.0.0.1" => ["127.0.0.1"],
            "::1" => ["::1"],
            _ when bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) => ["localhost"],
            _ => [bindAddress],
        };
    }

    private static IReadOnlyList<string> ResolveLanHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = addressInformation.Address;

                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    var text = address.ToString();

                    if (text.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    hosts.Add(text);
                }
            }
        }
        catch
        {
        }

        if (hosts.Count == 0)
        {
            hosts.Add("localhost");
        }

        return hosts
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeUriHost(string host)
    {
        return host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
    }

    private static string GuessContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json; charset=utf-8",
            // `TOAST-0092`. A TON document is text a reader is meant to *read*, so it is served
            // as plain text rather than downloaded as an unknown binary.
            ".txt" or ".log" or ".md" or ".ton" or ".tosh" => "text/plain; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".tsv" => "text/tab-separated-values; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }

    private static string FormatFileSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    private static bool TokensMatch(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return string.Equals(
            NormalizeToken(candidate),
            NormalizeToken(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToken(string token)
    {
        var builder = new StringBuilder(token.Length);

        foreach (var character in token)
        {
            if (character == '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private sealed record MultipartFileUpload(string FileName, byte[] Content);
}
