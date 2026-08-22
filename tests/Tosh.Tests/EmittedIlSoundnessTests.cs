using System.Reflection;
using System.Reflection.Emit;

namespace Tosh.Tests;

/// <summary>
/// Structural checks on emitted IL — `TOAST-0044`.
/// </summary>
/// <remarks>
/// <para>
/// Written because the obvious check does not work. `RuntimeHelpers.PrepareMethod` reports
/// success for IL that throws `InvalidProgramException` when the program actually runs —
/// verified on `bench/probes/toast-0044-repro.tosh`, whose bytes are identical in-process
/// and on disk and which passes preparation either way.
/// </para>
/// <para>
/// These decode the method body instead and assert two things the runtime requires:
/// every branch lands on an instruction boundary, and every `finally` handler ends with
/// `endfinally`. Both were violated by real emitter defects that nothing else caught — a
/// single-byte instruction dropped between two labels, and a handler whose recorded end ran
/// past its `endfinally` onto the method's `ret`.
/// </para>
/// </remarks>
public static class EmittedIl
{
    private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodeTable();

    private static Dictionary<short, OpCode> BuildOpcodeTable()
    {
        var map = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op) { map[op.Value] = op; }
        }
        return map;
    }

    /// <summary>Every fault found in one assembly, empty when it is sound.</summary>
    public static IReadOnlyList<string> Faults(Assembly assembly)
    {
        var faults = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            var members = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

            foreach (var member in members)
            {
                var body = member.GetMethodBody();
                var il = body?.GetILAsByteArray();
                if (body is null || il is null || il.Length == 0) { continue; }

                var where = $"{type.Name}.{member.Name}";
                var (starts, branches, desync) = Decode(il);

                if (desync)
                {
                    faults.Add($"{where}: the instruction stream does not decode cleanly");
                    continue;
                }

                foreach (var (at, op, target) in branches)
                {
                    if (target != il.Length && !starts.Contains(target))
                    {
                        faults.Add($"{where}: IL_{at:X4} {op} branches to IL_{target:X4}, " +
                                   "which is not an instruction boundary");
                    }
                }

                foreach (var clause in body.ExceptionHandlingClauses)
                {
                    if (clause.Flags != ExceptionHandlingClauseOptions.Finally) { continue; }

                    var end = clause.HandlerOffset + clause.HandlerLength;
                    if (end < 1 || end > il.Length) { continue; }

                    if (il[end - 1] != 0xDC)
                    {
                        faults.Add($"{where}: a finally handler ends at IL_{end - 1:X4} with " +
                                   $"0x{il[end - 1]:X2} rather than endfinally");
                    }
                }
            }
        }

        return faults;
    }

    private static (HashSet<int> Starts, List<(int At, string Op, int Target)> Branches, bool Desync)
        Decode(byte[] il)
    {
        var starts = new HashSet<int>();
        var branches = new List<(int, string, int)>();
        var i = 0;

        while (i < il.Length)
        {
            starts.Add(i);
            var offset = i;

            short value;
            if (il[i] == 0xFE && i + 1 < il.Length) { value = (short)(0xFE00 | il[i + 1]); i += 2; }
            else { value = il[i]; i += 1; }

            if (!Opcodes.TryGetValue(value, out var op)) { return (starts, branches, true); }

            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                    branches.Add((offset, op.Name!, offset + 2 + (sbyte)il[i])); i += 1; break;
                case OperandType.InlineBrTarget:
                    branches.Add((offset, op.Name!, offset + 5 + BitConverter.ToInt32(il, i))); i += 4; break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.ShortInlineR: i += 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, i);
                    i += 4;
                    var afterTable = i + (count * 4);
                    for (var k = 0; k < count; k++)
                    {
                        branches.Add((offset, "switch", afterTable + BitConverter.ToInt32(il, i + (k * 4))));
                    }
                    i = afterTable;
                    break;
                default: i += 4; break;
            }

            if (i > il.Length) { return (starts, branches, true); }
        }

        return (starts, branches, false);
    }
}

/// <summary>
/// The reproduction that `TOAST-0044` was reduced to still emits sound IL.
/// </summary>
/// <remarks>
/// This is the regression guard, and it is a *file* rather than a corpus case for a reason
/// worth stating: three synthetic cases using `or` and `and` in a loop condition were added
/// to the differential corpus and **none of them reproduce the defect** — reverting the fix
/// leaves all of them passing. The dropped byte needs the surrounding method to put the two
/// labels at particular offsets, which small examples do not.
///
/// So the corpus cases document the shape and this one holds the line.
/// </remarks>
public sealed class EmittedIlSoundnessTests
{
    [Fact]
    public void The_reproduction_emits_sound_il()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var path = Path.Combine(root, "bench/probes/toast-0044-repro.tosh");
        Assert.True(File.Exists(path), $"missing {path}");

        var runtime = Tosh.Runtime.ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);
        var parse = engine.Parse(File.ReadAllText(path), path);
        Assert.True(parse.Diagnostics.Count == 0, string.Join(", ", parse.Diagnostics));

        var unit = Tosh.Language.Binding.Lowerer.Lower(parse, runtime.Commands);
        using var stream = new MemoryStream();
        var result = Tosh.Compiler.BoundUnitEmitter.Emit(unit, $"ToshRepro_{Guid.NewGuid():N}", stream);
        Assert.True(result.IsClean, string.Join(", ", result.UnsupportedShapes));

        var faults = EmittedIl.Faults(Assembly.Load(stream.ToArray()));
        Assert.True(faults.Count == 0, "unsound IL:\n  " + string.Join("\n  ", faults));
    }
}
