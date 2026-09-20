namespace Tosh.Language;

/// <summary>
/// Whether a type-parameter constraint name means anything, and what the author probably
/// meant when it does not.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0055`. Four places validate a <c>where</c> clause — a class, a record, an
/// interface, and a generic function's arguments — and each one ended the same way: a name
/// it could not resolve was accepted, on the stated ground that refusing what cannot be
/// checked turns an unenforced constraint into a wrong error.
/// </para>
/// <para>
/// That reasoning is right about a name which resolves to something this session cannot
/// check *yet*, and wrong about a name which resolves to nothing at all. The second is a
/// typo, and accepting it does not leave the constraint unchecked — it deletes it, while the
/// declaration goes on reading as though it constrained something. Telling the two apart is
/// the whole of this file: recognition is a question about the *name*, asked before any
/// question about the type argument.
/// </para>
/// </remarks>
public sealed partial class ToshEngine
{
    /// <summary>
    /// Whether a constraint name names anything: a built-in, a CLR type, or a declaration.
    /// </summary>
    internal bool IsRecognisedConstraintName(string constraintName)
    {
        if (string.IsNullOrWhiteSpace(constraintName))
        {
            return false;
        }

        if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out _))
        {
            return true;
        }

        // `IComparable<T>` is recognised by its open name once the argument is stripped: the
        // substituted form is what gets resolved later, and it does not exist yet here.
        var bare = StripConstraintTypeArguments(constraintName);

        return TryResolveTypeName(constraintName) is not null
            || TryResolveTypeName(bare) is not null
            || TryGetNamedType(constraintName, out _)
            || TryGetNamedType(bare, out _);
    }

    /// <summary>The nearest constraint name to one that resolves to nothing, if any is near.</summary>
    /// <remarks>
    /// Built-ins and declared types both, because a misspelt <c>Numeric</c> and a misspelt
    /// <c>Drawable</c> are the same mistake and the author does not sort them that way. The
    /// distance bound is the one the refinement suggester uses, for the same reason: a
    /// suggestion that is not obviously right is worse than none.
    /// </remarks>
    internal string? SuggestConstraintName(string constraintName)
    {
        var normalized = StripConstraintTypeArguments(constraintName);

        if (normalized.Length == 0)
        {
            return null;
        }

        var candidates = new List<string>(ToshTypeParameterConstraintRegistry.KnownNames);

        foreach (var (name, _) in LanguageRuntime.Classes)
        {
            candidates.Add(name);
        }

        var best = (Name: (string?)null, Distance: int.MaxValue);

        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(
                normalized.ToLowerInvariant(),
                candidate.ToLowerInvariant());

            if (distance < best.Distance)
            {
                best = (candidate, distance);
            }
        }

        return best.Name is not null &&
               best.Distance <= Math.Max(2, Math.Max(normalized.Length, best.Name.Length) * 2 / 5)
            ? best.Name
            : null;
    }

    /// <summary>The help line offered when a constraint name resolves to nothing.</summary>
    internal string UnrecognisedConstraintHelp(string constraintName)
        => SuggestConstraintName(constraintName) is { } suggestion
            ? $"did you mean '{suggestion}'? A constraint names a built-in, a CLR type, an "
                + "interface, a trait or a class."
            : "a constraint names a built-in, a CLR type, an interface, a trait or a class. "
                + $"Built-ins: {string.Join(", ", ToshTypeParameterConstraintRegistry.KnownNames)}.";

    private static string StripConstraintTypeArguments(string typeName)
    {
        var angle = typeName.IndexOf('<', StringComparison.Ordinal);

        return (angle < 0 ? typeName : typeName[..angle]).Trim();
    }
}
