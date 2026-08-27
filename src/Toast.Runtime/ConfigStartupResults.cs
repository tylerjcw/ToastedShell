namespace Tosh.Runtime;

public sealed record ConfigInitializationResult(
    string RootDirectory,
    string ConfigFilePath,
    string ProfileFilePath,
    string AutoloadDirectory,
    IReadOnlyList<string> CreatedPaths);

public sealed record ConfigReloadResult(
    string RootDirectory,
    string ConfigFilePath,
    string ProfileFilePath,
    string AutoloadDirectory,
    IReadOnlyList<string> LoadedPaths);
