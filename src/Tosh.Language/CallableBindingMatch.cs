namespace Tosh.Language;

internal readonly record struct CallableBindingMatch<TCandidate>(
    TCandidate Candidate,
    Dictionary<string, object?> Locals,
    int Score);
