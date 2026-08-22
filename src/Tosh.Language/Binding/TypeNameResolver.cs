using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding;

/// <summary>
/// Parses and resolves textual type annotations (the <c>string?
/// TypeName</c> strings the parser stores on declarations) into
/// <see cref="BoundType"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Recognised grammar (informal):
/// <code>
/// type        = postfix ('?' )?
/// postfix     = primary ('[' ']')*
/// primary     = qualified ('&lt;' type (',' type)* '&gt;')?
///             | '(' type (',' type)* ')'
/// qualified   = Ident ('.' Ident)*
/// </code>
/// Recognised primitive aliases:
/// <c>int</c>, <c>int32</c>, <c>long</c>, <c>int64</c>, <c>short</c>,
/// <c>int16</c>, <c>byte</c>, <c>sbyte</c>, <c>uint</c>, <c>ulong</c>,
/// <c>ushort</c>, <c>float</c>, <c>double</c>, <c>decimal</c>,
/// <c>bool</c>, <c>string</c>, <c>char</c>, <c>object</c>,
/// <c>void</c>, <c>dynamic</c>, <c>any</c>, <c>nothing</c>.
/// </para>
/// <para>
/// Recognised generic shorthands:
/// <c>list&lt;T&gt;</c>, <c>dict&lt;K,V&gt;</c>, <c>set&lt;T&gt;</c>,
/// <c>array&lt;T&gt;</c> (alias for <c>T[]</c>),
/// <c>tuple&lt;T1,…,Tn&gt;</c>.
/// </para>
/// <para>
/// User-defined types (classes, records, structs, unions, enums,
/// interfaces, traits, refinements) are resolved by name through
/// the optional <see cref="UserTypes"/> dictionary supplied at
/// construction. Anything that doesn't match a primitive, a known
/// shorthand, or a user-type entry is forwarded to the optional
/// runtime <see cref="ITypeResolver"/> for last-chance CLR
/// resolution. Failure to resolve returns
/// <see cref="BoundType.Dynamic"/> and reports a diagnostic via the
/// supplied <see cref="OnDiagnostic"/> callback.
/// </para>
/// <para>
/// This resolver is deliberately syntactic — it does no type
/// inference, no checking, no substitution. Its only job is
/// "string in, BoundType out". Higher-level passes
/// (<see cref="TypeChecker"/> in T2) add semantic validation on top.
/// </para>
/// </remarks>
public sealed class TypeNameResolver
{
    private readonly IReadOnlyDictionary<string, BoundType>? _userTypes;
    private readonly ITypeResolver? _clrResolver;
    private readonly Action<string>? _onDiagnostic;

    public TypeNameResolver(
        IReadOnlyDictionary<string, BoundType>? userTypes = null,
        ITypeResolver? clrResolver = null,
        Action<string>? onDiagnostic = null)
    {
        _userTypes = userTypes;
        _clrResolver = clrResolver;
        _onDiagnostic = onDiagnostic;
    }

    /// <summary>The dictionary supplied at construction. Read-only.</summary>
    public IReadOnlyDictionary<string, BoundType>? UserTypes => _userTypes;

    /// <summary>
    /// Resolves <paramref name="typeName"/> to a
    /// <see cref="BoundType"/>. Returns
    /// <see cref="BoundType.Dynamic"/> when the input is null,
    /// empty, or unresolvable. The diagnostic callback only fires
    /// for unresolvable non-empty input.
    /// </summary>
    public BoundType Resolve(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return BoundType.Dynamic;
        var parser = new TypeNameParser(typeName);
        if (!parser.TryParse(out var node))
        {
            _onDiagnostic?.Invoke($"could not parse type name '{typeName}'");
            return BoundType.Dynamic;
        }
        return ResolveNode(node, typeName);
    }

    private BoundType ResolveNode(TypeNameNode node, string sourceText)
    {
        switch (node)
        {
            case NullableNode n:
                return new NullableType(ResolveNode(n.Inner, sourceText));

            case ArrayNode a:
                return new ArrayType(ResolveNode(a.Element, sourceText));

            case TupleNode t:
                return new TupleType(t.Elements.Select(e => ResolveNode(e, sourceText)).ToList());

            case FunctionNode f:
                return new FunctionType(
                    f.Parameters.Select(parameter => ResolveNode(parameter, sourceText)).ToList(),
                    ResolveNode(f.Return, sourceText));

            case GenericNode g:
                return ResolveGeneric(g, sourceText);

            case NamedNode named:
                return ResolveNamed(named, sourceText);

            default:
                return BoundType.Dynamic;
        }
    }

    private BoundType ResolveGeneric(GenericNode g, string sourceText)
    {
        var args = g.Arguments.Select(a => ResolveNode(a, sourceText)).ToList();
        switch (g.Name.ToLowerInvariant())
        {
            case "list" when args.Count == 1:
                return new ListType(args[0]);
            case "dict" when args.Count == 2:
                return new DictType(args[0], args[1]);
            case "hashtable" when args.Count == 2:
                return new DictType(args[0], args[1]);
            case "set" when args.Count == 1:
                return new SetType(args[0]);
            case "array" when args.Count == 1:
                return new ArrayType(args[0]);
            case "tuple":
                return new TupleType(args);
        }

        // Try user types — generic instantiation of a user template.
        if (_userTypes is not null && _userTypes.TryGetValue(g.Name, out var template))
        {
            // Generic type-alias substitution. When the template is a
            // `RefinementType` carrying a `TypeAliasStatementSyntax`
            // with declared type parameters, substitute the supplied
            // arguments for the parameter names and re-resolve the
            // alias's base-type text. This produces a precise
            // structural type (e.g. `MyList<string>` ⇒
            // `RefinementType(ListType(string), "MyList<string>",
            // alias)`) instead of the lenient
            // `GenericInstanceType` wrap that would otherwise leave
            // the parameter slots as `Dynamic`. Refinement clauses
            // are preserved on the wrapper so dynamic validation
            // still fires; the displayed name reflects the
            // instantiated form for diagnostics.
            if (template is RefinementType { Annotation: TypeAliasStatementSyntax aliasSyntax } aliasRef
                && aliasSyntax.TypeParameters.Count > 0)
            {
                if (aliasSyntax.TypeParameters.Count != args.Count)
                {
                    _onDiagnostic?.Invoke(
                        $"type alias '{g.Name}' expects {aliasSyntax.TypeParameters.Count} type "
                        + $"argument(s) but {args.Count} were supplied in '{sourceText}'");
                    return new GenericInstanceType(template, args);
                }

                var overlay = new Dictionary<string, BoundType>(StringComparer.Ordinal);
                if (_userTypes is not null)
                {
                    foreach (var kv in _userTypes) overlay[kv.Key] = kv.Value;
                }
                for (var i = 0; i < aliasSyntax.TypeParameters.Count; i++)
                {
                    overlay[aliasSyntax.TypeParameters[i]] = args[i];
                }
                var childResolver = new TypeNameResolver(overlay, _clrResolver, _onDiagnostic);
                var substitutedBase = childResolver.Resolve(aliasSyntax.BaseTypeName);
                var instantiatedName = $"{g.Name}<{string.Join(", ", args.Select(a => a.DisplayName))}>";
                return new RefinementType(substitutedBase, instantiatedName, aliasSyntax);
            }

            // Reject type arguments on a non-generic alias.
            if (template is RefinementType { Annotation: TypeAliasStatementSyntax aliasNoTp }
                && aliasNoTp.TypeParameters.Count == 0)
            {
                _onDiagnostic?.Invoke(
                    $"type alias '{g.Name}' is not generic but was used with type arguments in '{sourceText}'");
                return template;
            }

            return new GenericInstanceType(template, args);
        }

        // Last resort: ask the CLR resolver for the open generic and
        // construct it ourselves when every argument is concrete.
        if (_clrResolver is not null)
        {
            var arity = args.Count;
            var openName = g.Name.Contains('`') ? g.Name : $"{g.Name}`{arity}";
            var clrOpen = _clrResolver.Resolve(openName) ?? _clrResolver.Resolve(g.Name);
            if (clrOpen is { IsGenericTypeDefinition: true })
            {
                return new GenericInstanceType(BoundType.FromClr(clrOpen), args);
            }
        }

        _onDiagnostic?.Invoke($"unknown generic type '{g.Name}' in annotation '{sourceText}'");
        return BoundType.Dynamic;
    }

    private BoundType ResolveNamed(NamedNode named, string sourceText)
    {
        var name = named.QualifiedName;

        // 1. Primitive / sentinel aliases.
        if (TryResolvePrimitive(name, out var primitive)) return primitive!;

        // 2. User-declared types (classes, records, refinements, etc.)
        if (_userTypes is not null && _userTypes.TryGetValue(name, out var userType))
        {
            return userType;
        }

        // Runtime aliases are part of the language's built-in type surface too.
        // Consulting the shared table keeps compile-time annotations aligned with
        // the interpreter even when this syntactic resolver has no CLR fallback.
        // User types stay ahead of this broad alias table so adding a friendly CLR
        // alias cannot silently capture an existing source-defined type name.
        if (DotNetTypeResolver.BuiltInAliases.TryGetValue(name, out var builtIn))
        {
            return BoundType.FromClr(builtIn);
        }

        // 3. CLR fallback.
        if (_clrResolver is not null)
        {
            var clr = _clrResolver.Resolve(name);
            if (clr is not null) return BoundType.FromClr(clr);
        }

        // 4. The platform index — `TOAST-0034`.
        //
        // Static, so it needs no runtime instance and does not compromise this resolver
        // running without one. It is the same index `is` consults for a bare CLR name
        // (`TOAST-0029`) and the compiled `new` for a bare type name (`TOAST-0030`), so
        // adding it here aligns annotation resolution with what those two already answer
        // rather than inventing a fourth opinion.
        //
        // Without it `var h = new System.Collections.Hashtable()` reported that the type
        // could not be pinned down, while `new K()` for a class declared in the same file
        // resolved — an asymmetry with no reason a reader could see.
        if (DotNetTypeResolver.TryResolveKnownType(name, out var known) && known is not null)
        {
            return BoundType.FromClr(known);
        }

        _onDiagnostic?.Invoke($"unknown type '{name}' in annotation '{sourceText}'");
        return BoundType.Dynamic;
    }

    private static readonly Dictionary<string, BoundType> s_primitives =
        new(StringComparer.Ordinal)
        {
            ["int"] = BoundType.FromClr(typeof(int)),
            ["int32"] = BoundType.FromClr(typeof(int)),
            ["long"] = BoundType.FromClr(typeof(long)),
            ["int64"] = BoundType.FromClr(typeof(long)),
            ["short"] = BoundType.FromClr(typeof(short)),
            ["int16"] = BoundType.FromClr(typeof(short)),
            ["byte"] = BoundType.FromClr(typeof(byte)),
            ["sbyte"] = BoundType.FromClr(typeof(sbyte)),
            ["uint"] = BoundType.FromClr(typeof(uint)),
            ["ulong"] = BoundType.FromClr(typeof(ulong)),
            ["ushort"] = BoundType.FromClr(typeof(ushort)),
            ["float"] = BoundType.FromClr(typeof(float)),
            ["double"] = BoundType.FromClr(typeof(double)),
            ["decimal"] = BoundType.FromClr(typeof(decimal)),
            ["bool"] = BoundType.FromClr(typeof(bool)),
            ["string"] = BoundType.FromClr(typeof(string)),
            ["char"] = BoundType.FromClr(typeof(char)),
            ["object"] = BoundType.FromClr(typeof(object)),
            ["void"] = BoundType.Void,
            ["nothing"] = BoundType.Void,
            ["dynamic"] = BoundType.Dynamic,
            ["any"] = BoundType.Dynamic,
        };

    /// <summary>Whether the given name maps to a built-in primitive alias.</summary>
    public static bool IsPrimitiveAlias(string? name) =>
        name is not null && s_primitives.ContainsKey(name);

    private static bool TryResolvePrimitive(string name, out BoundType? type)
    {
        if (s_primitives.TryGetValue(name, out var resolved))
        {
            type = resolved;
            return true;
        }
        type = null;
        return false;
    }

    // --- internal type-name AST + parser ---

    internal abstract record TypeNameNode;
    internal sealed record NamedNode(string QualifiedName) : TypeNameNode;
    internal sealed record GenericNode(string Name, IReadOnlyList<TypeNameNode> Arguments) : TypeNameNode;
    internal sealed record ArrayNode(TypeNameNode Element) : TypeNameNode;
    internal sealed record NullableNode(TypeNameNode Inner) : TypeNameNode;
    internal sealed record TupleNode(IReadOnlyList<TypeNameNode> Elements) : TypeNameNode;

    /// <summary>`func(int) -> int` — <c>TOAST-0036</c>.</summary>
    /// <remarks>
    /// The grammar had no function node at all, which is why <see cref="FunctionType"/>
    /// existed in the bound tree and was never constructed: the representation was done and
    /// there was no way to write one.
    /// </remarks>
    internal sealed record FunctionNode(
        IReadOnlyList<TypeNameNode> Parameters,
        TypeNameNode Return) : TypeNameNode;

    private sealed class TypeNameParser
    {
        private readonly string _src;
        private int _pos;

        public TypeNameParser(string src)
        {
            _src = src;
            _pos = 0;
        }

        public bool TryParse(out TypeNameNode node)
        {
            node = null!;
            SkipWs();
            if (!TryParseType(out var t)) return false;
            SkipWs();
            if (_pos != _src.Length) return false;
            node = t;
            return true;
        }

        private bool TryParseType(out TypeNameNode node)
        {
            node = null!;
            if (!TryParsePostfix(out var inner)) return false;
            SkipWs();
            if (Peek('?'))
            {
                _pos++;
                node = new NullableNode(inner);
            }
            else
            {
                node = inner;
            }
            return true;
        }

        private bool TryParsePostfix(out TypeNameNode node)
        {
            node = null!;
            if (!TryParsePrimary(out var primary)) return false;
            while (true)
            {
                SkipWs();
                if (Peek('[') && _pos + 1 < _src.Length && _src[_pos + 1] == ']')
                {
                    _pos += 2;
                    primary = new ArrayNode(primary);
                    continue;
                }
                break;
            }
            node = primary;
            return true;
        }

        private bool TryParsePrimary(out TypeNameNode node)
        {
            node = null!;
            SkipWs();

            // `TOAST-0036`. `func(a, b) -> r`, right-associative so that
            // `func(int) -> func(int) -> int` is a function returning a function: the return
            // is parsed with the full type parser, which consumes as much as it can.
            if (TryParseFunctionType(out var functionNode))
            {
                node = functionNode;
                return true;
            }

            if (Peek('('))
            {
                _pos++;
                var elems = new List<TypeNameNode>();
                SkipWs();
                if (!Peek(')'))
                {
                    while (true)
                    {
                        if (!TryParseType(out var e)) return false;
                        elems.Add(e);
                        SkipWs();
                        if (Peek(','))
                        {
                            _pos++;
                            SkipWs();
                            continue;
                        }
                        break;
                    }
                }
                if (!Peek(')')) return false;
                _pos++;
                node = elems.Count == 1 ? elems[0] : new TupleNode(elems);
                return true;
            }

            if (!TryParseQualifiedName(out var qname)) return false;
            SkipWs();
            if (Peek('<'))
            {
                _pos++;
                var args = new List<TypeNameNode>();
                while (true)
                {
                    SkipWs();
                    if (!TryParseType(out var arg)) return false;
                    args.Add(arg);
                    SkipWs();
                    if (Peek(','))
                    {
                        _pos++;
                        continue;
                    }
                    break;
                }
                if (!Peek('>')) return false;
                _pos++;
                node = new GenericNode(qname, args);
                return true;
            }

            node = new NamedNode(qname);
            return true;
        }

        /// <summary>Parses `func(…) -> r`, or declines without consuming.</summary>
        /// <remarks>
        /// A bare `func` is deliberately not a function type here — it is left to the named
        /// path, so that "some callable" and "a callable of this shape" stay distinguishable.
        /// </remarks>
        private bool TryParseFunctionType(out TypeNameNode node)
        {
            node = null!;
            var start = _pos;

            if (!TryParseIdent())
            {
                _pos = start;
                return false;
            }

            if (!_src.AsSpan(start, _pos - start).Equals("func", StringComparison.OrdinalIgnoreCase))
            {
                _pos = start;
                return false;
            }

            SkipWs();
            if (!Peek('('))
            {
                _pos = start;
                return false;
            }

            _pos++;
            var parameters = new List<TypeNameNode>();
            SkipWs();

            if (!Peek(')'))
            {
                while (true)
                {
                    if (!TryParseType(out var parameter))
                    {
                        _pos = start;
                        return false;
                    }

                    parameters.Add(parameter);
                    SkipWs();

                    if (Peek(','))
                    {
                        _pos++;
                        SkipWs();
                        continue;
                    }

                    break;
                }
            }

            if (!Peek(')'))
            {
                _pos = start;
                return false;
            }

            _pos++;
            SkipWs();

            if (!Peek('-') || _pos + 1 >= _src.Length || _src[_pos + 1] != '>')
            {
                _pos = start;
                return false;
            }

            _pos += 2;
            SkipWs();

            if (!TryParseType(out var returnType))
            {
                _pos = start;
                return false;
            }

            node = new FunctionNode(parameters, returnType);
            return true;
        }

        private bool TryParseQualifiedName(out string name)
        {
            name = string.Empty;
            SkipWs();
            var start = _pos;
            if (!TryParseIdent()) return false;
            while (Peek('.'))
            {
                var save = _pos;
                _pos++;
                if (!TryParseIdent())
                {
                    _pos = save;
                    break;
                }
            }
            name = _src.Substring(start, _pos - start);
            return true;
        }

        private bool TryParseIdent()
        {
            if (_pos >= _src.Length) return false;
            var c = _src[_pos];
            if (!IsIdentStart(c)) return false;
            _pos++;
            while (_pos < _src.Length && IsIdentPart(_src[_pos])) _pos++;
            return true;
        }

        private static bool IsIdentStart(char c) =>
            char.IsLetter(c) || c == '_';

        private static bool IsIdentPart(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '`';

        private bool Peek(char c) =>
            _pos < _src.Length && _src[_pos] == c;

        private void SkipWs()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }
    }
}
