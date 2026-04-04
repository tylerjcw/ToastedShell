namespace Tosh.Language;

internal sealed record ToshRequiredScriptArtifact(
    string Path,
    ModuleExportTable Exports);
