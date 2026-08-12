namespace Tosh.Language.Binding;

/// <summary>
/// How to answer a bare name that turns out to be a member of the class it sits inside —
/// <c>TS-P2-41</c>.
/// </summary>
/// <remarks>
/// <para>
/// The resolution rule is not at fault and is not changed here: members are reached through
/// <c>ClassName.</c> or <c>$this.</c>, and a bare <c>f()</c> fails from an instance method
/// exactly as it does from a static one. What was wrong is the account given of the failure.
/// <c>static prop Y =&gt; f()</c> beside <c>static func f()</c> answered "did you mean 'df',
/// 'fg', or 'if'?" — three unrelated shell commands, when a member of the enclosing class
/// differs by nothing at all.
/// </para>
/// <para>
/// The shell has two suggestion machines, the binder's and the engine's, and this programme has
/// already watched one guard get fixed in one of them and come back through the other
/// (<c>TS-P1-24</c>). So the rule lives here once and both call it.
/// </para>
/// </remarks>
internal static class EnclosingMemberSuggestion
{
    /// <summary>
    /// Spells <paramref name="memberName"/> the way it has to be written to resolve: a static
    /// member through its class, an instance member through <c>$this</c>.
    /// </summary>
    /// <remarks>
    /// The qualifier follows the *member*, not the position referring to it, because that is what
    /// governs which spelling works — <c>$this.f()</c> from an instance method and
    /// <c>C.f()</c> from a static one both resolve today.
    /// </remarks>
    public static string Qualify(string className, string memberName, bool isStatic, bool isMethod)
    {
        var qualifier = isStatic ? $"{className}." : "$this.";
        return isMethod ? $"{qualifier}{memberName}()" : $"{qualifier}{memberName}";
    }

    /// <summary>
    /// Names the failure without calling it an unknown command, which it is not.
    /// </summary>
    /// <remarks>
    /// Worded to hold for a primary-constructor parameter as well as a member: both are declared
    /// by the class and neither is reachable as a bare name, but only members are reachable
    /// through a qualifier, so the specific fix belongs in <see cref="Label"/>.
    /// </remarks>
    public static string Title(string name, string className) =>
        $"'{name}' is declared by '{className}', but a bare name does not reach it.";

    /// <summary>The suggestion line, offered ahead of any command-name near-matches.</summary>
    public static string Label(string qualified) => $"did you mean '{qualified}'?";

    /// <summary>States the rule, which the diagnostic previously left the reader to infer.</summary>
    public static string Help(string className) =>
        $"a bare name in a class body is resolved as a command, not as a member of '{className}'.";

    /// <summary>
    /// The answer for a primary-constructor parameter referred to where it is not in scope.
    /// </summary>
    /// <remarks>
    /// Such a parameter is in scope in a property initializer and nowhere else — measured, after a
    /// first draft of this fix asserted the opposite and turned the working <c>prop X = x</c> into
    /// an error. It is not a member either: <c>$p.x</c> fails from outside. So there is no
    /// qualified spelling to offer, and the useful thing to say is where it *does* reach.
    /// </remarks>
    public static string OutOfScopeTitle(string name, string className) =>
        $"'{name}' is a constructor parameter of '{className}' and is not in scope here.";

    public static string OutOfScopeLabel(string name) => $"'{name}' is not a member and has no qualified form";

    public static string OutOfScopeHelp(string name) =>
        "a primary-constructor parameter is in scope only in a property initializer. " +
        $"Declare 'prop {name} = {name}' to reach it from the rest of the class.";
}
