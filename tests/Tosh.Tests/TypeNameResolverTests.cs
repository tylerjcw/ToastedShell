using Tosh.Language.Binding;
using Tosh.Compiler.IR;

namespace Tosh.Tests;

/// <summary>
/// Tests for <see cref="TypeNameResolver"/> — the binder-time
/// type-annotation parser/resolver that maps tosh syntax type
/// strings (<c>"int"</c>, <c>"list&lt;int&gt;"</c>,
/// <c>"dict&lt;string, MyClass&gt;"</c>, <c>"Foo[]"</c>,
/// <c>"int?"</c>, <c>"(int, string)"</c>) into the
/// <see cref="BoundType"/> hierarchy.
/// </summary>
public sealed class TypeNameResolverTests
{
    [Theory]
    [InlineData("int", typeof(int))]
    // `TOAST-0034`. Not in the alias map — that is keyed by tōast's spellings — but a real
    // CLR type name, which the platform index now answers. It used to fall back to dynamic,
    // which is why the assertion below used to branch.
    [InlineData("Int32", typeof(int))]
    [InlineData("long", typeof(long))]
    [InlineData("string", typeof(string))]
    [InlineData("bool", typeof(bool))]
    [InlineData("double", typeof(double))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("byte", typeof(byte))]
    [InlineData("char", typeof(char))]
    [InlineData("object", typeof(object))]
    public void Resolves_primitive_aliases(string name, Type expected)
    {
        var r = new TypeNameResolver();
        var t = r.Resolve(name);

        // Every name here is a real type by one spelling or the other, so every one
        // resolves concretely. This used to branch on `IsPrimitiveAlias` because a CLR name
        // that was not also a tōast alias had nowhere to resolve — `TOAST-0034` gave the
        // resolver the platform index, so the branch was describing a limitation rather
        // than a rule.
        Assert.True(t.IsConcrete, $"'{name}' did not resolve to a concrete type");
        Assert.Equal(expected, t.ClrType);
    }

    /// <summary>A name that is not a type at all still falls back to dynamic.</summary>
    /// <remarks>
    /// The control for the platform-index fallback. Consulting a broad index could have
    /// resolved almost anything, and "resolves concretely" would then be worthless.
    /// </remarks>
    [Theory]
    [InlineData("NotAnyTypeAnywhere")]
    [InlineData("Zzz.Qqq.Nope")]
    public void An_unknown_name_is_still_dynamic(string name)
        => Assert.True(new TypeNameResolver().Resolve(name).IsDynamic);

    [Fact]
    public void Void_alias_returns_void_singleton()
    {
        var r = new TypeNameResolver();
        Assert.Same(BoundType.Void, r.Resolve("void"));
        Assert.Same(BoundType.Void, r.Resolve("nothing"));
    }

    [Fact]
    public void Dynamic_alias_returns_dynamic_singleton()
    {
        var r = new TypeNameResolver();
        Assert.Same(BoundType.Dynamic, r.Resolve("dynamic"));
        Assert.Same(BoundType.Dynamic, r.Resolve("any"));
    }

    [Fact]
    public void Null_or_empty_returns_dynamic()
    {
        var r = new TypeNameResolver();
        Assert.Same(BoundType.Dynamic, r.Resolve(null));
        Assert.Same(BoundType.Dynamic, r.Resolve(""));
        Assert.Same(BoundType.Dynamic, r.Resolve("   "));
    }

    [Fact]
    public void List_of_int_is_listtype_with_concrete_element()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<ListType>(r.Resolve("list<int>"));
        Assert.Equal(typeof(int), t.Element.ClrType);
        Assert.Equal(typeof(List<int>), t.ClrType);
    }

    [Fact]
    public void Dict_of_string_int_resolves_both_args()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<DictType>(r.Resolve("dict<string, int>"));
        Assert.Equal(typeof(string), t.Key.ClrType);
        Assert.Equal(typeof(int), t.Value.ClrType);
        Assert.Equal(typeof(Dictionary<string, int>), t.ClrType);
    }

    [Fact]
    public void Set_of_int_is_settype()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<SetType>(r.Resolve("set<int>"));
        Assert.Equal(typeof(int), t.Element.ClrType);
        Assert.Equal(typeof(HashSet<int>), t.ClrType);
    }

    [Fact]
    public void Array_suffix_produces_arraytype()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<ArrayType>(r.Resolve("int[]"));
        Assert.Equal(typeof(int), t.Element.ClrType);
        Assert.Equal(typeof(int[]), t.ClrType);
    }

    [Fact]
    public void Nested_array_suffix_produces_jagged_array()
    {
        var r = new TypeNameResolver();
        var outer = Assert.IsType<ArrayType>(r.Resolve("int[][]"));
        var inner = Assert.IsType<ArrayType>(outer.Element);
        Assert.Equal(typeof(int), inner.Element.ClrType);
        Assert.Equal(typeof(int[][]), outer.ClrType);
    }

    [Fact]
    public void Nullable_int_wraps_in_nullabletype()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<NullableType>(r.Resolve("int?"));
        Assert.Equal(typeof(int), t.Inner.ClrType);
        Assert.Equal(typeof(int?), t.ClrType);
    }

    [Fact]
    public void Nullable_string_keeps_string_clr_type()
    {
        // Reference types stay reference types under nullable —
        // there's no Nullable<string> in CLR.
        var r = new TypeNameResolver();
        var t = Assert.IsType<NullableType>(r.Resolve("string?"));
        Assert.Equal(typeof(string), t.ClrType);
    }

    [Fact]
    public void Tuple_of_int_string_produces_tupletype()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<TupleType>(r.Resolve("(int, string)"));
        Assert.Equal(2, t.Elements.Count);
        Assert.Equal(typeof(int), t.Elements[0].ClrType);
        Assert.Equal(typeof(string), t.Elements[1].ClrType);
    }

    [Fact]
    public void Single_paren_is_grouping_not_tuple()
    {
        // `(int)` should be the same as `int` — single-element
        // parenthesised types collapse to the inner type.
        var r = new TypeNameResolver();
        var t = r.Resolve("(int)");
        Assert.Equal(typeof(int), t.ClrType);
        Assert.IsNotType<TupleType>(t);
    }

    [Fact]
    public void List_of_list_of_int_produces_nested_listtype()
    {
        var r = new TypeNameResolver();
        var outer = Assert.IsType<ListType>(r.Resolve("list<list<int>>"));
        var inner = Assert.IsType<ListType>(outer.Element);
        Assert.Equal(typeof(int), inner.Element.ClrType);
    }

    [Fact]
    public void List_with_dynamic_element_falls_back_to_ilist_clr_backing()
    {
        var r = new TypeNameResolver();
        var t = Assert.IsType<ListType>(r.Resolve("list<dynamic>"));
        Assert.True(t.Element.IsDynamic);
        Assert.Equal(typeof(System.Collections.IList), t.ClrType);
    }

    [Fact]
    public void User_type_lookup_via_dictionary()
    {
        var userTypes = new Dictionary<string, BoundType>(StringComparer.Ordinal)
        {
            ["MyClass"] = new UserClassType("MyClass", Definition: new object(), BackingClrType: null),
        };
        var r = new TypeNameResolver(userTypes: userTypes);
        var t = Assert.IsType<UserClassType>(r.Resolve("MyClass"));
        Assert.Equal("MyClass", t.Name);
    }

    [Fact]
    public void User_type_inside_list_resolves_through()
    {
        var userTypes = new Dictionary<string, BoundType>(StringComparer.Ordinal)
        {
            ["Email"] = new RefinementType(
                Base: BoundType.FromClr(typeof(string)),
                Name: "Email",
                Annotation: new object()),
        };
        var r = new TypeNameResolver(userTypes: userTypes);
        var t = Assert.IsType<ListType>(r.Resolve("list<Email>"));
        var refinement = Assert.IsType<RefinementType>(t.Element);
        Assert.Equal("Email", refinement.Name);
        Assert.Equal(typeof(string), refinement.ClrType);
    }

    [Fact]
    public void Unknown_type_returns_dynamic_and_emits_diagnostic()
    {
        var diagnostics = new List<string>();
        var r = new TypeNameResolver(onDiagnostic: diagnostics.Add);
        var t = r.Resolve("NoSuchType");
        Assert.Same(BoundType.Dynamic, t);
        Assert.Single(diagnostics);
        Assert.Contains("NoSuchType", diagnostics[0]);
    }

    [Fact]
    public void Malformed_input_returns_dynamic_and_emits_diagnostic()
    {
        var diagnostics = new List<string>();
        var r = new TypeNameResolver(onDiagnostic: diagnostics.Add);
        var t = r.Resolve("list<");   // unterminated generic
        Assert.Same(BoundType.Dynamic, t);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void Generic_user_type_produces_genericinstance()
    {
        var userTypes = new Dictionary<string, BoundType>(StringComparer.Ordinal)
        {
            ["Box"] = new UserClassType("Box", Definition: new object(), BackingClrType: null),
        };
        var r = new TypeNameResolver(userTypes: userTypes);
        var t = Assert.IsType<GenericInstanceType>(r.Resolve("Box<int>"));
        Assert.Equal("Box", ((UserClassType)t.Template).Name);
        Assert.Single(t.TypeArguments);
        Assert.Equal(typeof(int), t.TypeArguments[0].ClrType);
    }

    [Fact]
    public void DisplayName_round_trips_through_resolver()
    {
        // Primitive concrete types render via the CLR Type.Name
        // ("Int32", "String"), not the tosh alias. That's fine for a
        // diagnostic surface; the alias map is a one-way input.
        var r = new TypeNameResolver();
        Assert.Equal("list<Int32>", r.Resolve("list<int>").DisplayName);
        Assert.Equal("dict<String, Int32>", r.Resolve("dict<string, int>").DisplayName);
        Assert.Equal("Int32?", r.Resolve("int?").DisplayName);
        Assert.Equal("Int32[]", r.Resolve("int[]").DisplayName);
        Assert.Equal("(Int32, String)", r.Resolve("(int, string)").DisplayName);
    }
}
