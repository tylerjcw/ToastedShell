using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Key equality — the relation containers use, as §Key Equality states it. `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// The hashing box asked for "a contract consistent with equality". Measuring found there
/// was nothing to be consistent *with*: `==` is coercive, and coercion makes it
/// intransitive — `"1" == 1` and `1 == "1.0"` hold while `"1" == "1.0"` does not. A
/// relation with no equivalence classes cannot be hashed, so no hash function would have
/// fixed this; the relation had to be split in two.
/// </para>
/// <para>
/// The symptom was a dictionary that answered by insertion order. Two dictionaries built
/// from the same two pairs in opposite order returned **different values for the same
/// lookup**, because lookup was a linear scan using `==` and stopped at whichever
/// mutually-equal key it met first.
/// </para>
/// </remarks>
public sealed class ValueKeyEqualityTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// A dictionary answers the same however it was built.
    /// </summary>
    /// <remarks>
    /// The defect this item was really about. Both dictionaries hold both keys; before the
    /// split, `$d[1]` gave `"string-key"` from one and `"int-key"` from the other.
    /// </remarks>
    [Theory]
    [InlineData("$d[1]", "int-key")]
    [InlineData("$d[\"1\"]", "string-key")]
    public async Task A_lookup_does_not_depend_on_insertion_order(string lookup, string expected)
    {
        var forward = await RunAsync($"var d = {{% \"1\" => \"string-key\", 1 => \"int-key\" %}}\n{lookup}");
        var reversed = await RunAsync($"var d = {{% 1 => \"int-key\", \"1\" => \"string-key\" %}}\n{lookup}");

        Assert.Equal(expected, forward);
        Assert.Equal(expected, reversed);
    }

    /// <summary>
    /// Key equality is transitive exactly where `==` is not.
    /// </summary>
    [Fact]
    public void Key_equality_is_transitive_where_equality_is_not()
    {
        // `==` on the same three values: two equal to a third, unequal to each other.
        Assert.True(OperatorEvaluator.AreEqual("1", 1));
        Assert.True(OperatorEvaluator.AreEqual(1, "1.0"));
        Assert.False(OperatorEvaluator.AreEqual("1", "1.0"));

        // As keys, none of the three is the same value as another, so there is nothing to
        // be intransitive about.
        var keys = ShellKeyComparer.Instance;
        Assert.False(keys.Equals("1", 1));
        Assert.False(keys.Equals(1, "1.0"));
        Assert.False(keys.Equals("1", "1.0"));
    }

    /// <summary>Width is not part of a number's identity; textual resemblance is not identity.</summary>
    [Theory]
    [InlineData(1, 1.0, true)]
    [InlineData(1, 1L, true)]
    [InlineData(1, "1", false)]
    [InlineData(1.5, "1.5", false)]
    [InlineData(1, 2, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, "true", false)]
    public void Numbers_key_by_value_and_strings_key_only_to_strings(object left, object right, bool same)
    {
        Assert.Equal(same, ShellKeyComparer.Instance.Equals(left, right));

        if (same)
        {
            Assert.Equal(
                ShellKeyComparer.Instance.GetHashCode(left),
                ShellKeyComparer.Instance.GetHashCode(right));
        }
    }

    /// <summary>
    /// Equal values hash alike. This is the contract, asserted as a property.
    /// </summary>
    /// <remarks>
    /// A hash may collide for unequal values — that costs speed. The direction that must
    /// never fail is this one: two values the comparer calls the same must land in the
    /// same bucket, or a container holds both.
    /// </remarks>
    [Theory]
    [InlineData("{| a = 1, b = 2 |}", "{| b = 2, a = 1 |}")]
    [InlineData("[1, 2, 3]", "[1, 2, 3]")]
    [InlineData("[[1,2],[3]]", "[[1,2],[3]]")]
    [InlineData("1", "1.0")]
    [InlineData("\"abc\"", "\"abc\"")]
    [InlineData("null", "null")]
    public async Task Equal_keys_hash_alike(string left, string right)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var leftValue = (await engine.ExecuteToListAsync($"({left})"))[^1];
        var rightValue = (await engine.ExecuteToListAsync($"({right})"))[^1];

        Assert.True(
            ShellKeyComparer.Instance.Equals(leftValue, rightValue),
            "the fixture wants two values that are the same key");

        Assert.Equal(
            ShellKeyComparer.Instance.GetHashCode(leftValue),
            ShellKeyComparer.Instance.GetHashCode(rightValue));
    }

    /// <summary>
    /// Every container surface agrees, because they share one comparer.
    /// </summary>
    /// <remarks>
    /// Two records `==` calls equal survived `distinct`, `sort -u`, `frequencies` and a
    /// set literal as separate values, because each surface improvised its own key: a
    /// JSON rendering (field-order sensitive) in two of them, CLR `GetHashCode` in a
    /// third. The `"z"` is a sentinel — without it the surviving record is a lone value
    /// and `count` measures its fields instead of the number of items.
    /// </remarks>
    [Theory]
    [InlineData("distinct")]
    [InlineData("sort -u")]
    [InlineData("frequencies")]
    public async Task Reordered_records_are_one_key_on_every_surface(string surface)
        => Assert.Equal(
            "2",
            await RunAsync($"[{{| a = 1, b = 2 |}}, {{| b = 2, a = 1 |}}, \"z\"] | {surface} | count"));

    [Fact]
    public async Task A_set_literal_holds_one_of_two_equal_records()
        => Assert.Equal("1", await RunAsync("{: {| a = 1, b = 2 |}, {| b = 2, a = 1 |} :} | count"));

    /// <summary>Collections are the same key when their elements are, in order.</summary>
    [Theory]
    [InlineData("[[1,2],[1,2],\"z\"]", "2")]
    [InlineData("[[1,2],[2,1],\"z\"]", "3")]
    [InlineData("[[1,2],[1,2,3],\"z\"]", "3")]
    public async Task Collections_key_element_wise_and_in_order(string literal, string expected)
        => Assert.Equal(expected, await RunAsync($"{literal} | distinct | count"));

    /// <summary>
    /// A class instance is a key only to itself, unless the class declares `equals`.
    /// </summary>
    /// <remarks>
    /// The identity case needed a rule of its own: a class instance is an
    /// `IShellRecordObject` and therefore record-*like*, so the structural path folded two
    /// distinct instances holding equal properties. A type that overrides `Equals` decides
    /// its own identity, and that check has to precede the record path.
    ///
    /// The `equals`-without-`hash` case is the contract's awkward corner and is correct by
    /// construction: such a class hashes to a constant, so equal instances always share a
    /// bucket. Slower within it, never a wrong answer.
    /// </remarks>
    [Theory]
    [InlineData("class P(x: int) { prop X: int = $x }", "P", "3")]
    [InlineData("class P(x: int) { prop X: int = $x\n func equals(o) -> bool => ($this.X == $o.X) }", "P", "2")]
    [InlineData("class P(x: int) { prop X: int = $x\n func equals(o) -> bool => ($this.X == $o.X)\n func GetHashCode() -> int => $this.X }", "P", "2")]
    public async Task A_class_instance_keys_by_identity_unless_it_declares_equals(
        string declaration,
        string name,
        string expected)
        => Assert.Equal(
            expected,
            await RunAsync($"{declaration}\n[new {name}(1), new {name}(1), \"z\"] | distinct | count"));

    /// <summary>An enum member keys with its own enum, not with its backing number.</summary>
    [Theory]
    [InlineData("enum E { A, B }\n[E.A, E.A, \"z\"] | distinct | count", "2")]
    [InlineData("enum E { A, B }\n[E.A, E.B, \"z\"] | distinct | count", "3")]
    [InlineData("enum E { A, B }\n[E.A, 0, \"z\"] | distinct | count", "3")]
    public async Task An_enum_member_keys_within_its_own_enum(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// `distinct` gives the same answer whatever order its input arrived in.
    /// </summary>
    /// <remarks>
    /// The property that intransitivity destroys, and the reason the relation was split
    /// rather than the hash merely fixed.
    /// </remarks>
    [Theory]
    [InlineData("[1, \"1\", \"1.0\"]", "[\"1.0\", \"1\", 1]")]
    [InlineData("[{| a = 1, b = 2 |}, {| b = 2, a = 1 |}]", "[{| b = 2, a = 1 |}, {| a = 1, b = 2 |}]")]
    public async Task Distinct_does_not_depend_on_input_order(string forward, string reversed)
        => Assert.Equal(
            await RunAsync($"{forward} | distinct | count"),
            await RunAsync($"{reversed} | distinct | count"));
}
