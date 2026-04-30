namespace Tosh.Runtime;

public sealed record HelpPipelineInputInfo(
    bool Object,
    bool Scalar,
    bool PathLike,
    bool Collection,
    string? Notes = null);
