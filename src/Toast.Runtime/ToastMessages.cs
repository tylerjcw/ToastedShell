namespace Tosh.Runtime;

/// <summary>
/// The wording of a diagnostic raised from more than one place — `TOAST-0030` cause C.
/// </summary>
/// <remarks>
/// <para>
/// A message is part of the behaviour a specification describes, not decoration on top of
/// it. `§What null Means` says reaching a member of `null` reports it *and* says how to opt
/// out, so raising `NullReferenceException: member access 'Length' on null target` would
/// not implement that sentence.
/// </para>
/// <para>
/// Kept in one shared place rather than as strings at each raise site, so the language and
/// the runtime's operator evaluator cannot drift apart in what they say.
/// </para>
/// </remarks>
public static class ToastMessages
{
    /// <summary>Reaching a member of <see langword="null"/>, and how to opt out.</summary>
    public static string MemberOfNull(string memberPath) =>
        $"Cannot read member '{memberPath}' of null. Use '?.' to yield null instead.";

    /// <summary>An arithmetic operator given <see langword="null"/>.</summary>
    public static string NullOperand(string @operator) =>
        $"The '{@operator}' operator requires non-null operands.";

    /// <summary>
    /// Concatenation with <see langword="null"/>, which needs the opt-in spelled.
    /// </summary>
    /// <remarks>
    /// `TOAST-0018` made this raise. It used to render as the empty string, so
    /// `null + "a"` was `"a"` while `null + 1` raised — a missing value vanishing silently
    /// into concatenated output. The guidance is the reason raising is acceptable, so a
    /// raise site that drops it has kept the breakage and lost the remedy.
    /// </remarks>
    public static string NullStringConcatenation =>
        NullOperand("+") + " Use '?? \"\"' to treat null as empty text.";

    /// <summary>A function result refused by its declared return annotation.</summary>
    public static string FunctionReturnConversionFailure(string functionName, string typeName) =>
        $"Function '{functionName}' returned a value that could not be converted to '{typeName}'.";

    /// <summary>The source label paired with <see cref="FunctionReturnConversionFailure"/>.</summary>
    public static string FunctionReturnConversionLabel(string typeName) =>
        $"the returned value does not match '{typeName}'";
}
