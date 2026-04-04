namespace Tosh.Language;

internal sealed record NativeLibraryBinding(
    string Target,
    string CacheKey,
    IntPtr Handle,
    ModuleExportTable Exports);
