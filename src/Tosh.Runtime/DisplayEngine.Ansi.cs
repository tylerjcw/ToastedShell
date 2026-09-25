using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Tosh.Runtime;

public sealed partial class DisplayEngine
{
    private static bool TryFormatPrettyScalar(
        object value,
        out string typeName,
        out string valueText,
        out DisplayTableAlignment valueAlignment)
    {
        typeName = string.Empty;
        valueText = string.Empty;
        valueAlignment = DisplayTableAlignment.Left;

        if (value is ShellTextLine or StyledText or Type or Enum)
        {
            return false;
        }

        var runtimeType = value.GetType();

        if (value is string text)
        {
            typeName = runtimeType.Name;
            valueText = text;
            return true;
        }

        if (value is char character)
        {
            typeName = runtimeType.Name;
            valueText = character.ToString();
            return true;
        }

        if (value is bool boolean)
        {
            typeName = runtimeType.Name;
            valueText = boolean.ToString().ToLowerInvariant();
            return true;
        }

        if (value is DateTime dateTime)
        {
            typeName = runtimeType.Name;
            valueText = dateTime.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            typeName = runtimeType.Name;
            valueText = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Uri uri)
        {
            typeName = runtimeType.Name;
            valueText = uri.ToString();
            return true;
        }

        if (value is ToshVector vector)
        {
            typeName = vector.ShellTypeName;
            valueText = vector.ToString(null, CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Complex complex)
        {
            typeName = ComplexShellType.Instance.ShellTypeName;
            valueText = ComplexShellType.FormatCompact(complex);
            return true;
        }

        if (value is Guid guid)
        {
            typeName = runtimeType.Name;
            valueText = guid.ToString();
            return true;
        }

        if (value is TimeSpan timeSpan)
        {
            typeName = runtimeType.Name;
            valueText = timeSpan.ToString("c", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Units.Quantity quantity)
        {
            typeName = quantity.CategoryName;
            valueText = quantity.ToString();
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if (value is decimal decimalValue)
        {
            typeName = runtimeType.Name;
            valueText = decimalValue.ToString(CultureInfo.InvariantCulture);
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if (value is BigInteger bigInteger)
        {
            typeName = runtimeType.Name;
            valueText = bigInteger.ToString("N0", CultureInfo.InvariantCulture);
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if (IsIntegralScalarType(runtimeType) && value is IConvertible convertible)
        {
            typeName = runtimeType.Name;
            valueText = Convert.ToDecimal(convertible, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if ((runtimeType == typeof(float) || runtimeType == typeof(double)) &&
            value is IFormattable floatingPoint)
        {
            typeName = runtimeType.Name;
            valueText = floatingPoint.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty;
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        return false;
    }

    private bool TryFormatProfileBackedPrettyScalar(
        object value,
        DisplayRenderOptions options,
        out string typeName,
        out string valueText,
        out DisplayTableAlignment valueAlignment)
    {
        typeName = string.Empty;
        valueText = string.Empty;
        valueAlignment = DisplayTableAlignment.Left;

        if (value is ShellTextLine or StyledText or ICommandResult or Type)
        {
            return false;
        }

        var runtimeType = value.GetType();

        if (!IsProfileBackedPrettyScalarType(runtimeType))
        {
            return false;
        }

        var profile = _profiles.Resolve(runtimeType);

        if (profile is null || profile.TargetType != runtimeType)
        {
            return false;
        }

        var tableContext = new DisplayTableContext(runtimeType, [value], options);

        if (profile.TryBuildTable(tableContext, out _))
        {
            return false;
        }

        var renderContext = new DisplayValueContext(
            value,
            DisplaySurface.Root,
            options.Style,
            RenderOptions: options,
            FormattingOptions: new ObjectFormattingOptions(options.Style));

        if (!profile.TryRender(renderContext, out valueText) || string.IsNullOrWhiteSpace(valueText))
        {
            return false;
        }

        typeName = ObjectFormatter.GetTypeName(runtimeType);
        valueAlignment = IsRightAlignedType(runtimeType) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left;
        return true;
    }

    private static bool IsProfileBackedPrettyScalarType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        return effectiveType == typeof(DateTime) ||
               effectiveType == typeof(DateOnly) ||
               effectiveType == typeof(DateTimeOffset) ||
               effectiveType == typeof(TimeOnly) ||
               effectiveType == typeof(TimeSpan) ||
               effectiveType == typeof(StorageSize) ||
               effectiveType == typeof(TemporalAmount) ||
               effectiveType == typeof(Complex) ||
               typeof(Units.Quantity).IsAssignableFrom(effectiveType) ||
               effectiveType == typeof(Uri) ||
               effectiveType == typeof(IPAddress) ||
               effectiveType == typeof(UnixFileMode) ||
               effectiveType == typeof(FileAttributes) ||
               effectiveType == typeof(FileSystemPrincipalInfo) ||
               effectiveType == typeof(FileSystemEntryType) ||
               effectiveType == typeof(ShellJobStatus) ||
               effectiveType == typeof(HelpSubjectKind) ||
               effectiveType == typeof(CommandResolutionKind);
    }

    private static bool IsIntegralScalarType(Type type)
    {
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(BigInteger) ||
               type == typeof(nint) ||
               type == typeof(nuint);
    }

    private static bool IsMixedNumericScalarType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return IsIntegralScalarType(effectiveType) ||
               effectiveType == typeof(decimal) ||
               effectiveType == typeof(float) ||
               effectiveType == typeof(double);
    }

    private static bool ShouldPreferValueList(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is null || !ObjectFormatter.TryFormatSimple(value, isRoot: true, out _))
            {
                return false;
            }
        }

        return true;
    }

    private string ApplyValueStyling(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text.Trim();

        if (trimmed.Length == 0 || trimmed.Length > 2)
        {
            return text;
        }

        var theme = TableTheme;

        return trimmed switch
        {
            "✓" or "✔" => theme.SuccessGlyph.Apply(text).ToAnsi(),
            "✗" or "✘" => theme.ErrorGlyph.Apply(text).ToAnsi(),
            "⚠" => theme.WarningGlyph.Apply(text).ToAnsi(),
            _ => text,
        };
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
    }

    // Soft-wrap a cell's content to a column width, preferring space breaks
    // and falling back to hard splits. Caps wrapped output at
    // MaxWrappedLinesPerCell so a paragraph-sized field doesn't blow up
    // row height; the overflow line ends with "…" to signal truncation.
    // Lines that already contain ANSI escapes are left intact (downstream
    // ClipCell handles them) — wrapping styled text would require parsing
    // escape sequences and is deferred.
    private const int MaxWrappedLinesPerCell = 8;

    private static IReadOnlyList<string> WrapCellLines(string cell, int width, bool isHeader)
    {
        var inputLines = SplitLines(cell);

        if (isHeader || width <= 1)
        {
            return inputLines;
        }

        // Skip wrapping for cells that look structural (nested tables, ANSI
        // styling, or multi-line composite content). Breaking those on
        // spaces shatters their layout. Single-line prose is the target.
        if (inputLines.Count > 1 || ContainsStructuralChars(cell))
        {
            return inputLines;
        }

        var result = new List<string>(1);

        foreach (var line in inputLines)
        {
            if (StyledText.GetVisibleLength(line) <= width)
            {
                result.Add(line);
            }
            else
            {
                WrapPlainLine(line, width, result);
            }

            if (result.Count >= MaxWrappedLinesPerCell)
            {
                break;
            }
        }

        if (result.Count > MaxWrappedLinesPerCell)
        {
            var trimmed = result.Take(MaxWrappedLinesPerCell - 1).ToList();
            var lastFull = result[MaxWrappedLinesPerCell - 1];
            // `TUI-0005`, the same character-index cut as the two ClipCell twins: `Length`
            // is code units and `width` is columns, so this both mismeasured and sliced
            // mid-character. `Elide` marks the cut itself when the text does not fit.
            trimmed.Add(lastFull.Length > 0
                ? (StyledText.GetVisibleLength(lastFull) > width - 1
                    ? TextMeasure.Elide(lastFull, width)
                    : lastFull + "…")
                : "…");
            return trimmed;
        }

        return result;
    }

    private static bool ContainsStructuralChars(string text)
    {
        foreach (var ch in text)
        {
            // ESC for ANSI styling, or any Unicode box-drawing / block char.
            if (ch == '\x1b' || (ch >= '\u2500' && ch <= '\u259F'))
            {
                return true;
            }
        }

        return false;
    }

    private static void WrapPlainLine(string line, int width, List<string> output)
    {
        // First wrapped line gets a 4-space indent — a visual cue that
        // anchors "row N starts here" when neighbouring rows fit on one
        // line. Continuation lines stay flush so the wrap reads as a
        // single paragraph.
        const string FirstLineIndent = "    ";
        var firstBudget = Math.Max(1, width - FirstLineIndent.Length);
        var i = 0;
        var isFirst = true;

        while (i < line.Length)
        {
            var budget = isFirst ? firstBudget : width;
            var rest = line.AsSpan(i);

            if (TextMeasure.MeasureWidth(rest) <= budget)
            {
                output.Add(isFirst ? FirstLineIndent + line[i..] : line[i..]);
                return;
            }

            // `TUI-0005`. This measured the budget in characters — `line.AsSpan(i, budget)`
            // — so a 36-column cell was handed 36 CJK characters and drew 72 columns. The
            // walk counts what a terminal draws, and the break still prefers a space.
            var used = 0;
            var chars = 0;
            var lastSpace = -1;
            var firstClusterLength = 0;

            foreach (var cluster in TextMeasure.Clusters(rest))
            {
                if (firstClusterLength == 0)
                {
                    firstClusterLength = cluster.Length;
                }

                var clusterWidth = TextMeasure.ClusterWidth(cluster);

                if (used + clusterWidth > budget)
                {
                    break;
                }

                if (cluster.Length == 1 && cluster[0] == ' ')
                {
                    lastSpace = chars;
                }

                used += clusterWidth;
                chars += cluster.Length;
            }

            // A cluster wider than the whole budget still has to advance, and it advances
            // whole: taking one code unit of it would split the character.
            var take = lastSpace > 0 ? lastSpace : chars > 0 ? chars : firstClusterLength;
            var chunk = line.Substring(i, take);
            output.Add(isFirst ? FirstLineIndent + chunk : chunk);
            i += take;
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            isFirst = false;
        }
    }

    private static int GetCellDisplayWidth(string value)
    {
        return SplitLines(value).Max(StyledText.GetVisibleLength);
    }

    private static int GetRenderedRowHeight(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0)
        {
            return 0;
        }

        return cells.Max(cell => SplitLines(cell).Count);
    }

    private static string PadCellLeft(string value, int width)
    {
        var padding = Math.Max(0, width - StyledText.GetVisibleLength(value));
        return padding == 0 ? value : $"{new string(' ', padding)}{value}";
    }

    private static string PadCellRight(string value, int width)
    {
        var padding = Math.Max(0, width - StyledText.GetVisibleLength(value));
        return padding == 0 ? value : $"{value}{new string(' ', padding)}";
    }


}
