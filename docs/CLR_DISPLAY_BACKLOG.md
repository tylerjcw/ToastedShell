# ToSh CLR Display Backlog

## Goal

ToSh should provide first-class display profiles for CLR types that are:

- common in shell workflows
- structurally meaningful
- visually improved by a curated view over raw reflection

The working rule is:

- show all information that is useful and safe
- avoid leaking secrets or noisy internal state
- give rich standalone views and concise nested summaries

## Current Coverage

### Temporal and scalar-adjacent

- `DateTime`
- `DateTimeOffset`
- `DateOnly`
- `TimeOnly`
- `TimeSpan`
- `TemporalAmount`
- `StorageSize`
- `Guid`
- `Version`

### Reflection and runtime metadata

- `Type`
- `Assembly`
- `AssemblyName`
- `MethodBase`
- `PropertyInfo`
- `FieldInfo`
- `EventInfo`
- `ParameterInfo`
- `AssemblyLoadContext`

### Collections and helper types

- `KeyValuePair<TKey, TValue>`
- `Tuple`
- `ValueTuple`
- `DictionaryEntry`
- `Index`
- `Range`

### Networking and HTTP

- `IPAddress`
- `IPEndPoint`
- `DnsEndPoint`
- `Cookie`
- `CookieCollection`
- `CookieContainer`
- `NetworkCredential`
- `HttpRequestMessage`
- `HttpResponseMessage`
- `HttpHeaders`
- `HttpRequestHeaders`
- `HttpResponseHeaders`
- `HttpContentHeaders`
- `HttpContent`

### Diagnostics and process

- `Exception`
- `StackFrame`
- `StackTrace`
- `Process`
- `ProcessStartInfo`
- `ProcessModule`
- `FileVersionInfo`

### Filesystem and system

- `DriveInfo`
- `FileSystemWatcher`
- `UnixFileMode`
- `FileAttributes`
- `FileSystemPrincipalInfo`

### Culture, text, and misc

- `Uri`
- `Regex`
- `Encoding`
- `CultureInfo`
- `TimeZoneInfo`
- `byte[]`
- `Color`
- XML `XDocument` / `XElement`

## High-Value Next Batches

### Network information

- `NetworkInterface`
- `IPInterfaceProperties`
- `UnicastIPAddressInformation`
- `GatewayIPAddressInformation`
- `IPHostEntry`
- `PhysicalAddress`
- `PingOptions`
- `TcpConnectionInformation`

### HTTP and web

- `HttpMethod`
- `HttpStatusCode`
- `HttpVersionPolicy`
- `WebHeaderCollection`
- `WebProxy`
- `CookieException`
- `AuthenticationHeaderValue`
- `ContentDispositionHeaderValue`
- `EntityTagHeaderValue`
- `MediaTypeHeaderValue`
- `CacheControlHeaderValue`

### Streams and I/O

- `Stream`
- `FileStream`
- `MemoryStream`
- `BufferedStream`
- `StreamReader`
- `StreamWriter`
- `StringReader`
- `StringWriter`
- `ZipArchive`
- `ZipArchiveEntry`

### JSON and structured data

- `JsonDocument`
- `JsonElement`
- `JsonProperty`
- `JsonNode`
- `JsonObject`
- `JsonArray`
- `JsonValue`
- XML `XAttribute`, `XText`, `XComment`, `XCData`

### Platform and runtime

- `OperatingSystem`
- `OSPlatform`
- `Architecture`
- `RuntimeInformation`-derived values
- `AssemblyDependencyResolver`
- `RuntimeMethodHandle`
- `RuntimeTypeHandle`

### Security and identity

- `X509Certificate2`
- `X500DistinguishedName`
- `Oid`
- `Claim`
- `ClaimsIdentity`
- `ClaimsPrincipal`

### Numerics and geometry

- `BigInteger`
- `Complex`
- `Vector2`
- `Vector3`
- `Vector4`
- `Quaternion`
- `Matrix4x4`
- `Point`
- `Size`
- `Rectangle`

## Lower-Priority or Case-By-Case

- `Task`
- `ValueTask`
- `CancellationToken`
- `Lazy<T>`
- `WeakReference`
- `NameValueCollection`
- immutable and concurrent collection families

These can be valuable, but they should follow real shell usage rather than be added just to increase surface area.
