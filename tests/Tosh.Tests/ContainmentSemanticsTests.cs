using Tosh.Language;

namespace Tosh.Tests;

public sealed class ContainmentSemanticsTests
{
    [Fact]
    public async Task Strings_use_ordinal_substring_containment()
    {
        var engine = new ToshEngine();

        var values = await engine.ExecuteToListAsync(
            """
            echo ("Alphabet" contains "pha")
            echo ("Alphabet" contains "PHA")
            echo ("pha" in "Alphabet")
            echo ("PHA" in "Alphabet")
            """);

        Assert.Equal([true, false, true, false], values);
    }

    [Fact]
    public async Task Collections_search_elements_without_stringification_false_positives()
    {
        var engine = new ToshEngine();

        var values = await engine.ExecuteToListAsync(
            """
            echo ([1, 2, 3] contains 2)
            echo ([10] contains 1)
            echo (["alphabet"] contains "pha")
            echo (2 in [1, 2, 3])
            echo (42 contains 2)
            """);

        Assert.Equal([true, false, false, true, false], values);
    }

    [Fact]
    public async Task Dictionaries_search_keys_and_not_values()
    {
        var engine = new ToshEngine();

        var values = await engine.ExecuteToListAsync(
            """
            var person = { "name" => "Alice", "age" => 30 }
            echo ($person contains "name")
            echo ($person contains "Alice")
            echo ("name" in $person)
            echo ("Alice" in $person)
            """);

        Assert.Equal([true, false, true, false], values);
    }

    [Fact]
    public async Task Shell_native_sequences_search_their_items()
    {
        var engine = new ToshEngine();

        var values = await engine.ExecuteToListAsync(
            """
            class Bag {
                func enumerate() { return [10, 20, 30] }
            }

            class ScalarLabel {
                shy func ToString() -> string { return "needle" }
            }

            var bag = new Bag()
            var label = new ScalarLabel()
            echo ((1..3) contains 2)
            echo (2 in (1..3))
            echo ((1..3) contains 4)
            echo (4 in (1..3))
            echo ($bag contains 20)
            echo (20 in $bag)
            echo ($label contains "need")
            """);

        Assert.Equal([true, true, false, false, true, true, false], values);
    }

    [Fact]
    public async Task Collection_membership_uses_canonical_class_equality()
    {
        var engine = new ToshEngine();

        var values = await engine.ExecuteToListAsync(
            """
            class ValueProbe(value: string) {
                prop Value: string = value
                shy func Equals(other) -> bool {
                    return ($this.Value == $other.Value)
                }
            }

            var left = new ValueProbe("same")
            var right = new ValueProbe("same")
            var different = new ValueProbe("different")
            echo ([$right] contains $left)
            echo ([$different] contains $left)
            """);

        Assert.Equal([true, false], values);
    }
}
