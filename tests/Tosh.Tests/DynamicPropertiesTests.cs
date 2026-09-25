using Tosh.Language;
using Tosh.Runtime;
using Xunit;

namespace Tosh.Tests;

public sealed class DynamicPropertiesTests
{
    private static ToshEngine CreateEngine() => new(ToshRuntime.CreateDefault().Language);

    [Fact]
    public async Task Fluid_record_accepts_dynamic_properties_and_indexer_unification()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid record MemInfo(MemTotal)
            var m = new MemInfo(128)
            $m.MemFree = 64
            $m["Buffers"] = 32
            echo $m.MemTotal
            echo $m.MemFree
            echo $m["MemFree"]
            echo $m.Buffers
            echo $m["Buffers"]
            """);

        Assert.Equal([128L, 64L, 64L, 32L, 32L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Closed_record_rejects_dynamic_properties()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                record ClosedRec(MemTotal)
                var m = new ClosedRec(128)
                $m.MemFree = 64
                """);
        });
    }

    [Fact]
    public async Task Fluid_class_accepts_dynamic_properties_and_indexer_unification()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid class Person {
                prop Name: string = "Alice"
            }
            var p = new Person()
            $p.Age = 30
            $p["City"] = "Wonderland"
            echo $p.Name
            echo $p.Age
            echo $p["Age"]
            echo $p.City
            echo $p["City"]
            """);

        Assert.Equal(["Alice", "30", "30", "Wonderland", "Wonderland"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Closed_class_rejects_dynamic_properties()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                class ClosedPerson {
                    prop Name: string = "Alice"
                }
                var p = new ClosedPerson()
                $p.Age = 30
                """);
        });
    }

    [Fact]
    public async Task Strict_and_fluid_conflict_is_rejected()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                strict fluid class Invalid {}
                """);
        });
    }

    [Fact]
    public async Task Anonymous_record_casts_to_fluid_record_retaining_extra_fields()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid record MemInfo(MemTotal, MemFree)
            var r = {| MemTotal: 128, MemFree: 64, Extra: 32 |} as MemInfo
            echo $r.MemTotal
            echo $r.MemFree
            echo $r.Extra
            echo ($r is MemInfo)
            """);

        Assert.Equal(["128", "64", "32", "True"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Anonymous_record_casts_to_closed_record_rejects_extra_fields()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                record ClosedInfo(MemTotal, MemFree)
                var r = {| MemTotal: 128, MemFree: 64, Extra: 32 |} as ClosedInfo
                """);
        });
    }

    [Fact]
    public async Task Members_add_string_style_adds_properties()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid class Item {}
            var item = new Item()
            var _ = ($item | members add "Title" "Widget")
            echo $item.Title
            """);

        Assert.Equal(["Widget"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Members_add_block_style_with_computed_property()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid class Item {}
            var item = new Item()
            var _ = ($item | members add {
                prop Title: string = "Widget"
                prop UpperTitle => $this.Title.ToUpper()
            })
            echo $item.Title
            echo $item.UpperTitle
            $item.Title = "Gadget"
            echo $item.UpperTitle
            """);

        Assert.Equal(["Widget", "WIDGET", "GADGET"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Members_add_computed_property_with_getter_and_setter()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid class Temperature {
                prop Celsius: double = 0.0
            }
            var t = new Temperature()
            var _ = ($t | members add "Fahrenheit" --get { return ($this.Celsius * 9.0 / 5.0 + 32.0) } --set { $this.Celsius = (($value - 32.0) * 5.0 / 9.0) })
            $t.Celsius = 100.0
            echo $t.Fahrenheit
            $t.Fahrenheit = 32.0
            echo $t.Celsius
            """);

        Assert.Equal(["212", "0"], results.Select(r => r?.ToString()!).ToArray());
    }

    [Fact]
    public async Task Members_add_rejects_closed_types()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                class ClosedClass {}
                var c = new ClosedClass()
                $c | members add "Field" 1
                """);
        });
    }

    [Fact]
    public async Task Members_add_rejects_shadowing_declared_property()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                fluid class DeclaredClass {
                    prop X: int = 1
                }
                var c = new DeclaredClass()
                $c | members add "X" 2
                """);
        });
    }

    [Fact]
    public async Task Members_add_rejects_computed_properties_on_records()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                fluid record Rec(X)
                var r = new Rec(1)
                $r | members add { prop Y => 2 }
                """);
        });
    }

    [Fact]
    public async Task Members_del_removes_dynamic_properties()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid class Item {}
            var item = new Item()
            var _ = ($item | members add {
                prop Title = "Widget"
                prop Age = 42
                prop Tag = "v1"
            })
            $item = ($item | members del "Title")
            $item = ($item | members del Age Tag)
            echo ($item | members | where _.Name != null | count)
            """);

        Assert.Equal([0L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Members_del_rejects_deleting_declared_properties()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                fluid class Item {
                    prop Declared = 10
                }
                var item = new Item()
                $item | members del "Declared"
                """);
        });
    }

    [Fact]
    public async Task User_declared_type_shadows_ambient_clr_type_in_as_cast()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            """
            fluid record CpuInfo()
            var raw = {| Architecture: "x86_64", CustomProp: 123 |}
            var info = $raw as CpuInfo
            echo $info.Architecture
            echo $info.CustomProp
            """);

        Assert.Equal(["x86_64", "123"], results.Select(r => r!.ToString()!).ToArray());
    }
}
