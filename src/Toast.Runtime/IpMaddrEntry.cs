namespace Tosh.Runtime;

public sealed record IpMaddrEntry(
    string? Family,
    string? Address,
    string? Link,
    int? Users);
