using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tosh.Crumb.Models;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Thin client for aurweb RPC v5. Used for search/info only; builds clone
/// from https://aur.archlinux.org/&lt;pkg&gt;.git separately.
///
/// Endpoints:
///   GET /rpc/v5/search/{arg}?by={field}
///   GET /rpc/v5/info?arg[]=foo&amp;arg[]=bar
/// </summary>
public sealed class AurClient : IDisposable
{
    public const string DefaultBaseUrl = "https://aur.archlinux.org";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public AurClient(HttpClient? http = null, string? baseUrl = null)
    {
        if (http is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl ?? DefaultBaseUrl) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("crumb/0.1 (+https://github.com/komradbobo/tosh)");
            _ownsClient = true;
        }
        else
        {
            _http = http;
            _ownsClient = false;
        }
    }

    public async Task<IReadOnlyList<Package>> SearchAsync(string term, string by = "name-desc", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return Array.Empty<Package>();
        var url = $"/rpc/v5/search/{Uri.EscapeDataString(term)}?by={Uri.EscapeDataString(by)}";
        var resp = await _http.GetFromJsonAsync<RpcResponse>(url, JsonOpts, ct);
        return ToPackages(resp);
    }

    /// <summary>
    /// AUR RPC <c>/info</c>. The aurweb HTTP endpoint limits request
    /// length, so callers passing &gt;100 names get chunked into
    /// batches automatically (paru does the same — see
    /// <c>raur::AurExt::cache_info</c>). Results are concatenated in
    /// arrival order; duplicates are not removed (the upstream
    /// endpoint deduplicates within a single batch).
    /// </summary>
    public async Task<IReadOnlyList<Package>> InfoAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var args = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        if (args.Length == 0) return Array.Empty<Package>();

        const int BatchSize = 100;
        var all = new List<Package>(args.Length);
        for (var off = 0; off < args.Length; off += BatchSize)
        {
            var batch = args.Skip(off).Take(BatchSize).ToArray();
            var qs = string.Join("&", batch.Select(a => $"arg[]={Uri.EscapeDataString(a)}"));
            var resp = await _http.GetFromJsonAsync<RpcResponse>($"/rpc/v5/info?{qs}", JsonOpts, ct);
            all.AddRange(ToPackages(resp));
        }
        return all;
    }

    private static IReadOnlyList<Package> ToPackages(RpcResponse? resp)
    {
        if (resp?.Results is null || resp.Results.Count == 0) return Array.Empty<Package>();
        var list = new List<Package>(resp.Results.Count);
        foreach (var r in resp.Results) list.Add(r.ToPackage());
        return list;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class RpcResponse
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("resultcount")] public int ResultCount { get; set; }
        [JsonPropertyName("results")] public List<RpcResult> Results { get; set; } = new();
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class RpcResult
    {
        [JsonPropertyName("Name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("PackageBase")] public string? PackageBase { get; set; }
        [JsonPropertyName("Version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("Description")] public string? Description { get; set; }
        [JsonPropertyName("URL")] public string? URL { get; set; }
        [JsonPropertyName("Maintainer")] public string? Maintainer { get; set; }
        [JsonPropertyName("NumVotes")] public int NumVotes { get; set; }
        [JsonPropertyName("Popularity")] public double Popularity { get; set; }
        [JsonPropertyName("OutOfDate")] public long? OutOfDate { get; set; }
        [JsonPropertyName("FirstSubmitted")] public long? FirstSubmitted { get; set; }
        [JsonPropertyName("LastModified")] public long? LastModified { get; set; }
        [JsonPropertyName("License")] public List<string>? License { get; set; }
        [JsonPropertyName("Depends")] public List<string>? Depends { get; set; }
        [JsonPropertyName("MakeDepends")] public List<string>? MakeDepends { get; set; }
        [JsonPropertyName("CheckDepends")] public List<string>? CheckDepends { get; set; }
        [JsonPropertyName("OptDepends")] public List<string>? OptDepends { get; set; }
        [JsonPropertyName("Provides")] public List<string>? Provides { get; set; }
        [JsonPropertyName("Conflicts")] public List<string>? Conflicts { get; set; }
        [JsonPropertyName("Replaces")] public List<string>? Replaces { get; set; }
        [JsonPropertyName("Groups")] public List<string>? Groups { get; set; }

        public Package ToPackage() => new()
        {
            Name = Name,
            Version = Version,
            Repo = "aur",
            Description = Description,
            Url = URL,
            Maintainer = Maintainer,
            Base = PackageBase,
            License = License is { Count: > 0 } ? string.Join(", ", License) : null,
            Votes = NumVotes,
            Popularity = Popularity,
            OutOfDate = OutOfDate is > 0 ? DateTimeOffset.FromUnixTimeSeconds(OutOfDate.Value) : null,
            FirstSubmitted = FirstSubmitted is > 0 ? DateTimeOffset.FromUnixTimeSeconds(FirstSubmitted.Value) : null,
            LastModified = LastModified is > 0 ? DateTimeOffset.FromUnixTimeSeconds(LastModified.Value) : null,
            Depends = Depends ?? (IReadOnlyList<string>)Array.Empty<string>(),
            MakeDepends = MakeDepends ?? (IReadOnlyList<string>)Array.Empty<string>(),
            CheckDepends = CheckDepends ?? (IReadOnlyList<string>)Array.Empty<string>(),
            OptDepends = OptDepends ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Provides = Provides ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Conflicts = Conflicts ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Replaces = Replaces ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Groups = Groups ?? (IReadOnlyList<string>)Array.Empty<string>(),
        };
    }
}
