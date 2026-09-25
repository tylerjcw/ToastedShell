namespace Tosh.Language.Binding;

// These enums are shared between the bound IR and the parser/language layers.

public enum DeclarationModifier
{
    Default,
    Shy,
    Global,
    Export,
}

[Flags]
public enum SubcommandModifier
{
    None = 0,
    Eager = 1 << 0,
    Hidden = 1 << 1,
    Hollow = 1 << 2,
    Vital = 1 << 3,
    Default = 1 << 4,
}

public enum ScriptInputDeclarationKind
{
    Flag,
    Argument,
}

public enum NativeParameterPassingMode
{
    In,
    Ref,
    Out,
}

public enum RedirectionStream
{
    Output,          // o> / out>
    Error,           // e> / err>
    OutputThenError, // o+e> / out+err>
    ErrorThenOutput, // e+o> / err+out>
}

public enum RedirectionMode
{
    Truncate,   // >
    Append,     // >>
}
