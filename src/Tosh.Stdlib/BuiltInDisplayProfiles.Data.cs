using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tosh.Stdlib.Shell;
using Tosh.Stdlib.Sys;
using Tosh.Runtime;
using Tosh.Stdlib.Net;

namespace Tosh.Stdlib;

public static partial class BuiltInDisplayProfiles
{
    internal static void RegisterDataProfiles(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        registry.Register(CreateDateTimeProfile(preferences));
        registry.Register(CreateDateTimeOffsetProfile(preferences));
        registry.Register(CreateDateOnlyProfile(preferences));
        registry.Register(CreateTimeOnlyProfile(preferences));
        registry.Register(CreateTimeSpanProfile(preferences));
        registry.Register(CreateTemporalAmountProfile(preferences));
        registry.Register(CreateStorageSizeProfile(preferences));
        registry.Register(CreateShellTextLineProfile());
        registry.Register(CreateColumnSummaryProfile());
        registry.Register(CreateGuidProfile());
        registry.Register(CreateVersionProfile());
        registry.Register(CreateByteArrayProfile());
        registry.Register(CreateUriProfile());
        registry.Register(CreateRegexProfile());
        registry.Register(CreateTimeZoneInfoProfile());
        registry.Register(CreateCultureInfoProfile());
        registry.Register(CreateEncodingProfile());
        registry.Register(CreateExceptionProfile());
        registry.Register(CreateKeyValuePairProfile());
        registry.Register(CreateTupleProfile());
        registry.Register(CreateHashSetProfile());
        registry.Register(CreateIndexProfile());
        registry.Register(CreateRangeProfile());
        registry.Register(CreateMethodBaseProfile());
        registry.Register(CreatePropertyInfoProfile());
        registry.Register(CreateStackFrameProfile());
        registry.Register(CreateStackTraceProfile());
        registry.Register(CreateDictionaryEntryProfile());
        registry.Register(CreateAssemblyNameProfile());
        registry.Register(CreateTypeProfile());
        registry.Register(CreateAssemblyProfile());
        registry.Register(CreateFieldInfoProfile());
        registry.Register(CreateEventInfoProfile());
        registry.Register(CreateParameterInfoProfile());
        registry.Register(CreateFileVersionInfoProfile());
        registry.Register(CreateAssemblyLoadContextProfile());
        registry.Register(CreateColorProfile());
        registry.Register(CreateEnumProfile());
        registry.Register(CreateTextStatisticsProfile());
        registry.Register(CreateDictionaryRecordProfile());
        registry.Register(CreateReadOnlyDictionaryRecordProfile());
        registry.Register(CreateShellRecordProfile());
        registry.Register(CreateShellStructProfile());
        registry.Register(CreateShellClassProfile());
        registry.Register(CreateShellUnionVariantProfile());
        registry.Register(CreateObjectKeyedDictionaryProfile());
        registry.Register(CreateGroupingInfoProfile());
        registry.Register(CreateHelpSubjectKindProfile());
        registry.Register(CreateHelpSummaryProfile());
        registry.Register(CreateHelpSearchResultProfile());
        registry.Register(CreateHelpTopicProfile());
        registry.Register(CreateHelpCategoryInfoProfile());
        registry.Register(CreateFormatterStatusProfile());
        registry.Register(CreateXDocumentProfile());
        registry.Register(CreateXElementProfile());
        registry.Register(CreateJsonDocumentProfile());
        registry.Register(CreateJsonElementProfile());
        registry.Register(CreateJsonPropertyProfile());
        registry.Register(CreateJsonNodeProfile());
        registry.Register(CreateJsonObjectProfile());
        registry.Register(CreateJsonArrayProfile());
        registry.Register(CreateJsonValueProfile());
        registry.Register(CreateStyledTextProfile());
        registry.Register(CreateX509Certificate2Profile());
        registry.Register(CreateX500DistinguishedNameProfile());
        registry.Register(CreateOidProfile());
        registry.Register(CreateClaimProfile());
        registry.Register(CreateClaimsIdentityProfile());
        registry.Register(CreateClaimsPrincipalProfile());
    }

    private static DisplayProfile CreateDateTimeProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<DateTime>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatDateTime(
                    (DateTime)context.Value,
                    preferences.DateTime.TableMode,
                    preferences.DateTime.TableFormat,
                    preferences.NowProvider))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatDateTime(
                    (DateTime)context.Value,
                    preferences.DateTime.ScalarMode,
                    preferences.DateTime.ScalarFormat,
                    preferences.NowProvider));
    }

    private static DisplayProfile CreateDateTimeOffsetProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<DateTimeOffset>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatDateTimeOffset(
                    (DateTimeOffset)context.Value,
                    preferences.DateTimeOffset.TableMode,
                    preferences.DateTimeOffset.TableFormat,
                    preferences.NowProvider))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatDateTimeOffset(
                    (DateTimeOffset)context.Value,
                    preferences.DateTimeOffset.ScalarMode,
                    preferences.DateTimeOffset.ScalarFormat,
                    preferences.NowProvider));
    }

    private static DisplayProfile CreateDateOnlyProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<DateOnly>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatDateOnly(
                    (DateOnly)context.Value,
                    preferences.DateOnly.TableMode,
                    preferences.DateOnly.TableFormat,
                    preferences.NowProvider))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatDateOnly(
                    (DateOnly)context.Value,
                    preferences.DateOnly.ScalarMode,
                    preferences.DateOnly.ScalarFormat,
                    preferences.NowProvider));
    }

    private static DisplayProfile CreateTimeOnlyProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<TimeOnly>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatTimeOnly(
                    (TimeOnly)context.Value,
                    preferences.TimeOnly.TableMode,
                    preferences.TimeOnly.TableFormat))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatTimeOnly(
                    (TimeOnly)context.Value,
                    preferences.TimeOnly.ScalarMode,
                    preferences.TimeOnly.ScalarFormat));
    }

    private static DisplayProfile CreateStorageSizeProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<StorageSize>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatStorageSize((StorageSize)context.Value, preferences.StorageSize.Mode));
    }

    private static DisplayProfile CreateTimeSpanProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<TimeSpan>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatTimeSpan(
                    (TimeSpan)context.Value,
                    preferences.TimeSpan.TableMode,
                    preferences.TimeSpan.TableFormat))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatTimeSpan(
                    (TimeSpan)context.Value,
                    preferences.TimeSpan.ScalarMode,
                    preferences.TimeSpan.ScalarFormat));
    }

    private static DisplayProfile CreateTemporalAmountProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<TemporalAmount>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatTemporalAmount(
                    (TemporalAmount)context.Value,
                    context.Surface == DisplaySurface.TableCell
                        ? preferences.TimeSpan.TableMode
                        : preferences.TimeSpan.ScalarMode,
                    context.Surface == DisplaySurface.TableCell
                        ? preferences.TimeSpan.TableFormat
                        : preferences.TimeSpan.ScalarFormat));
    }

    private static DisplayProfile CreateShellTextLineProfile()
    {
        return DisplayProfile
            .For<ShellTextLine>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ShellTextLine)context.Value).Text);
    }

    private static DisplayProfile CreateColumnSummaryProfile()
    {
        return DisplayProfile
            .For<ColumnSummary>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ColumnSummary)context.Value).Column)
            .AddTableCase(context => BuildColumnSummaryColumns(context.Rows))
            .AddSelectableTableColumns(_ => BuildColumnSummarySelectableColumns());
    }

    private static DisplayProfile CreateGuidProfile()
    {
        return DisplayProfile
            .For<Guid>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildGuidColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((Guid)context.Value).ToString("D", CultureInfo.InvariantCulture));
    }

    private static DisplayProfile CreateVersionProfile()
    {
        return DisplayProfile
            .For<Version>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildVersionColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((Version)context.Value).ToString());
    }

    private static DisplayProfile CreateByteArrayProfile()
    {
        return DisplayProfile
            .For<byte[]>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildByteArrayColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatByteArrayPreview((byte[])context.Value));
    }

    private static DisplayProfile CreateUriProfile()
    {
        return DisplayProfile
            .For<Uri>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildUriColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var uri = (Uri)context.Value;
                    return new StyledText(uri.ToString(), Link: uri.ToString()).ToAnsi();
                });
    }

    private static DisplayProfile CreateRegexProfile()
    {
        return DisplayProfile
            .For<Regex>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildRegexColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => $"/{((Regex)context.Value).ToString()}/");
    }

    private static DisplayProfile CreateTimeZoneInfoProfile()
    {
        return DisplayProfile
            .For<TimeZoneInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildTimeZoneInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((TimeZoneInfo)context.Value).Id);
    }

    private static DisplayProfile CreateCultureInfoProfile()
    {
        return DisplayProfile
            .For<CultureInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCultureInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var culture = (CultureInfo)context.Value;
                    return string.IsNullOrWhiteSpace(culture.Name) ? "<invariant>" : culture.Name;
                });
    }

    private static DisplayProfile CreateEncodingProfile()
    {
        return DisplayProfile
            .For<Encoding>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildEncodingColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((Encoding)context.Value).WebName);
    }

    private static DisplayProfile CreateExceptionProfile()
    {
        return DisplayProfile
            .For<Exception>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildExceptionColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((Exception)context.Value).Message);
    }

    private static DisplayProfile CreateKeyValuePairProfile()
    {
        return new DisplayProfile(typeof(KeyValuePair<,>))
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildKeyValuePairColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatKeyValuePairSummary(context.Value));
    }

    private static DisplayProfile CreateTupleProfile()
    {
        return DisplayProfile
            .For<ITuple>()
            .AddTableCase(_ => BuildTupleColumns(_.Rows))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatTuplePreview((ITuple)context.Value));
    }

    private static DisplayProfile CreateHashSetProfile()
    {
        return new DisplayProfile(typeof(HashSet<>))
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHashSetColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHashSetPreview(context.Value));
    }

    private static DisplayProfile CreateIndexProfile()
    {
        return DisplayProfile
            .For<Index>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildIndexColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatIndexValue((Index)context.Value));
    }

    private static DisplayProfile CreateRangeProfile()
    {
        return DisplayProfile
            .For<Range>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildRangeColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatRangeValue((Range)context.Value));
    }

    private static DisplayProfile CreateMethodBaseProfile()
    {
        return DisplayProfile
            .For<MethodBase>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildMethodBaseColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatMethodBaseSummary((MethodBase)context.Value));
    }

    private static DisplayProfile CreatePropertyInfoProfile()
    {
        return DisplayProfile
            .For<PropertyInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildPropertyInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatPropertyInfoSummary((PropertyInfo)context.Value));
    }

    private static DisplayProfile CreateStackFrameProfile()
    {
        return DisplayProfile
            .For<StackFrame>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildStackFrameColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatStackFrameSummary((StackFrame)context.Value));
    }

    private static DisplayProfile CreateStackTraceProfile()
    {
        return DisplayProfile
            .For<StackTrace>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildStackTraceColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatStackTraceSummary((StackTrace)context.Value));
    }

    private static DisplayProfile CreateDictionaryEntryProfile()
    {
        return DisplayProfile
            .For<DictionaryEntry>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildDictionaryEntryColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatDictionaryEntrySummary((DictionaryEntry)context.Value));
    }

    private static DisplayProfile CreateAssemblyNameProfile()
    {
        return DisplayProfile
            .For<AssemblyName>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildAssemblyNameColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((AssemblyName)context.Value).FullName ?? ((AssemblyName)context.Value).Name ?? "<unknown>");
    }

    private static DisplayProfile CreateTypeProfile()
    {
        return DisplayProfile
            .For<Type>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildTypeColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ReflectionMetadataUtilities.GetDisplayName((Type)context.Value));
    }

    private static DisplayProfile CreateAssemblyProfile()
    {
        return DisplayProfile
            .For<Assembly>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildAssemblyColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((Assembly)context.Value).GetName().FullName ?? ((Assembly)context.Value).GetName().Name ?? "<unknown>");
    }

    private static DisplayProfile CreateFieldInfoProfile()
    {
        return DisplayProfile
            .For<FieldInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFieldInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatFieldInfoSummary((FieldInfo)context.Value));
    }

    private static DisplayProfile CreateEventInfoProfile()
    {
        return DisplayProfile
            .For<EventInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildEventInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatEventInfoSummary((EventInfo)context.Value));
    }

    private static DisplayProfile CreateParameterInfoProfile()
    {
        return DisplayProfile
            .For<ParameterInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildParameterInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatParameterInfoSummary((ParameterInfo)context.Value));
    }

    private static DisplayProfile CreateFileVersionInfoProfile()
    {
        return DisplayProfile
            .For<FileVersionInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileVersionInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatFileVersionInfoSummary((FileVersionInfo)context.Value));
    }

    private static DisplayProfile CreateAssemblyLoadContextProfile()
    {
        return DisplayProfile
            .For<AssemblyLoadContext>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildAssemblyLoadContextColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatAssemblyLoadContextSummary((AssemblyLoadContext)context.Value));
    }

    private static DisplayProfile CreateColorProfile()
    {
        return DisplayProfile
            .For<Color>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatColorCell((Color)context.Value))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatColor((Color)context.Value))
            .AddTableCase(
                context => context.Rows.Count == 1 ? BuildSingleColorColumns() : BuildColorCollectionColumns());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSingleColorColumns()
    {
        const string swatch = "███████";

        return
        [
            new DisplayTableColumn("Sample", row =>
            {
                var hex = FormatColorHex((Color)row);
                var line = new StyledText(swatch, Foreground: hex).ToAnsi();
                return $"{line}\n{line}\n{line}";
            },
                MinWidth: 7, MaxWidth: 9, Priority: 0, CanHide: false, UseIndexTheme: false),
            new DisplayTableColumn("Name", row => $"\n{FormatColorName((Color)row)}\n",
                MinWidth: 4, MaxWidth: 24, Priority: 10),
            new DisplayTableColumn("Hex", row => $"\n{FormatColorHex((Color)row)}\n",
                MinWidth: 7, MaxWidth: 9, Priority: 20),
            new DisplayTableColumn("A", row => $"\n{((Color)row).A}\n",
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("R", row => $"\n{((Color)row).R}\n",
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("G", row => $"\n{((Color)row).G}\n",
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("B", row => $"\n{((Color)row).B}\n",
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("IsKnown", row => $"\n{(((Color)row).IsKnownColor ? "true" : "false")}\n",
                MinWidth: 5, MaxWidth: 7, Priority: 50),
            new DisplayTableColumn("IsNamed", row => $"\n{(((Color)row).IsNamedColor ? "true" : "false")}\n",
                MinWidth: 5, MaxWidth: 7, Priority: 50),
            new DisplayTableColumn("IsSystem", row => $"\n{(((Color)row).IsSystemColor ? "true" : "false")}\n",
                MinWidth: 5, MaxWidth: 8, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildColorCollectionColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => FormatColorName((Color)row),
                MinWidth: 4, MaxWidth: 24, Priority: 10),
            new DisplayTableColumn("Hex", row => FormatColorHex((Color)row),
                MinWidth: 7, MaxWidth: 9, Priority: 20),
            new DisplayTableColumn("A", row => ((Color)row).A,
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("R", row => ((Color)row).R,
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("G", row => ((Color)row).G,
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("B", row => ((Color)row).B,
                MinWidth: 3, MaxWidth: 5, Priority: 30, Alignment: DisplayTableAlignment.Right),
            new DisplayTableColumn("Sample", row =>
                new StyledText("███████", Foreground: FormatColorHex((Color)row)),
                MinWidth: 7, MaxWidth: 9, Priority: 0, CanHide: false),
        ];
    }

    private static DisplayProfile CreateEnumProfile()
    {
        return DisplayProfile
            .For<Enum>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => ReflectionMetadataUtilities.FormatEnumValue((Enum)context.Value, includeTypeName: false))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ReflectionMetadataUtilities.FormatEnumValue((Enum)context.Value, includeTypeName: true));
    }

    private static DisplayProfile CreateDictionaryRecordProfile()
    {
        return DisplayProfile
            .For<IDictionary<string, object?>>()
            .AddTableCase(context => BuildRecordColumns(context.Rows));
    }

    private static DisplayProfile CreateReadOnlyDictionaryRecordProfile()
    {
        return DisplayProfile
            .For<IReadOnlyDictionary<string, object?>>()
            .AddTableCase(context => BuildRecordColumns(context.Rows));
    }

    /// <summary>
    /// Declared records, structs, classes and union variants render like anonymous records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An anonymous record is an <c>ExpandoObject</c>, which is an
    /// <c>IDictionary&lt;string, object?&gt;</c> and so already matched the profile above. A
    /// declared record is a <c>ToshRecordInstance</c>, which is not — so it matched no profile
    /// at all and fell through to the display engine's generic record-like builder.
    /// </para>
    /// <para>
    /// The target is <c>ToshRecordInstance</c> rather than <c>IShellRecordObject</c>. The
    /// interface is broader than it looks: a <c>Quantity</c> implements it so that
    /// introspection can see its <c>base-value</c>, while display must keep rendering it as the
    /// scalar <c>483.06 MW</c>. Targeting the interface turned quantities into tables, which
    /// <c>ObjectFormatterTests</c> caught.
    /// </para>
    /// <para>
    /// That fallback drops any column whose values are not a renderable cell type, which
    /// silently removed every array- and collection-valued field. `record Trade(Give: array&lt;…&gt;,
    /// Receive: array&lt;…&gt;)` lost both of its columns and rendered as a bare type name, while the
    /// structurally identical anonymous record rendered in full. One row was unaffected, because
    /// the single-row path asks for structured values explicitly — so the same value rendered
    /// correctly alone and wrongly in a list of two.
    /// </para>
    /// </remarks>
    private static DisplayProfile CreateShellRecordProfile()
        => DisplayProfile
            .For<Tosh.Language.ToshRecordInstance>()
            .AddTableCase(context => BuildRecordColumns(context.Rows));

    private static DisplayProfile CreateShellStructProfile()
        => DisplayProfile
            .For<Tosh.Language.ToshStructInstance>()
            .AddTableCase(context => BuildRecordColumns(context.Rows));

    private static DisplayProfile CreateShellClassProfile()
        => DisplayProfile
            .For<Tosh.Language.ToshClassInstance>()
            .AddTableCase(context => BuildRecordColumns(context.Rows));

    private static DisplayProfile CreateShellUnionVariantProfile()
        => DisplayProfile
            .For<Tosh.Language.ToshUnionVariantInstance>()
            .AddTableCase(context => BuildRecordColumns(context.Rows));

    private static DisplayProfile CreateObjectKeyedDictionaryProfile()
    {
        return DisplayProfile
            .For<Dictionary<object, object?>>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                context => BuildObjectKeyedDictColumns((Dictionary<object, object?>)context.Rows[0]))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var dict = (Dictionary<object, object?>)context.Value;
                    // Rendered in the dict literal's own delimiters so displayed
                    // output round-trips as source (TS-P2-25). `{ ... }` opens a
                    // block now, so the previous rendering could not be pasted
                    // back into the shell.
                    // `{%%}` already says empty, and the annotation made the
                    // rendering unparseable where `{||}` and `{::}` are not.
                    if (dict.Count == 0)
                    {
                        return "{%%}";
                    }

                    var preview = string.Join(", ", dict.Take(4).Select(kv => $"{FormatDictKey(kv.Key)} => {FormatDisplaySummaryValue(kv.Value)}"));
                    return dict.Count > 4 ? $"{{% {preview}, ... %}} ({dict.Count} entries)" : $"{{% {preview} %}}";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildObjectKeyedDictColumns(Dictionary<object, object?> dict)
    {
        return dict.Select((kv, index) => new DisplayTableColumn(
            FormatDictKey(kv.Key),
            _ => kv.Value,
            Priority: index,
            CanHide: index > 0))
        .ToArray();
    }

    private static DisplayProfile CreateGroupingInfoProfile()
    {
        return DisplayProfile
            .For<GroupingInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var grouping = (GroupingInfo)context.Value;
                    var key = grouping.Key?.ToString() ?? "<null>";
                    return $"{key} ({grouping.Count})";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Key", row => ((GroupingInfo)row).Key, MinWidth: 3, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Count", row => ((GroupingInfo)row).Count, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 8, Priority: 10, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateHelpSubjectKindProfile()
    {
        return DisplayProfile
            .For<HelpSubjectKind>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((HelpSubjectKind)context.Value) switch
                {
                    HelpSubjectKind.BuiltIn => "built-in",
                    HelpSubjectKind.Alias => "alias",
                    HelpSubjectKind.Function => "function",
                    HelpSubjectKind.External => "external",
                    HelpSubjectKind.Language => "language",
                    HelpSubjectKind.Type => "type",
                    _ => context.Value.ToString() ?? string.Empty,
                });
    }

    private static DisplayProfile CreateHelpSummaryProfile()
    {
        return DisplayProfile
            .For<HelpSummary>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((HelpSummary)row).Name, MinWidth: 8, MaxWidth: 18, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((HelpSummary)row).Kind, MinWidth: 8, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Category", row => ((HelpSummary)row).Category, MinWidth: 8, MaxWidth: 14, Priority: 20),
                    new DisplayTableColumn("Description", row => ((HelpSummary)row).Description, MinWidth: 20, MaxWidth: 56, Priority: 30, CanHide: false),
                    new DisplayTableColumn("Aliases", row => string.Join(", ", ((HelpSummary)row).Aliases), MinWidth: 6, MaxWidth: 24, Priority: 40),
                    new DisplayTableColumn("Usage", row => ((HelpSummary)row).Usage, MinWidth: 16, MaxWidth: 48, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateHelpSearchResultProfile()
    {
        return DisplayProfile
            .For<HelpSearchResult>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Score", row => ((HelpSearchResult)row).Score.ToString("0.0", CultureInfo.InvariantCulture), MinWidth: 5, MaxWidth: 7, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Name", row => ((HelpSearchResult)row).Name, MinWidth: 8, MaxWidth: 18, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((HelpSearchResult)row).Kind, MinWidth: 8, MaxWidth: 10, Priority: 20),
                    new DisplayTableColumn("Category", row => ((HelpSearchResult)row).Category, MinWidth: 8, MaxWidth: 14, Priority: 30),
                    new DisplayTableColumn("Description", row => ((HelpSearchResult)row).Description, MinWidth: 20, MaxWidth: 56, Priority: 40, CanHide: false),
                    new DisplayTableColumn("Aliases", row => string.Join(", ", ((HelpSearchResult)row).Aliases), MinWidth: 6, MaxWidth: 24, Priority: 50),
                    new DisplayTableColumn("Usage", row => ((HelpSearchResult)row).Usage, MinWidth: 16, MaxWidth: 48, Priority: 60),
                ]);
    }

    private static DisplayProfile CreateHelpTopicProfile()
    {
        return DisplayProfile
            .For<HelpTopic>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((HelpTopic)row).Name, MinWidth: 8, MaxWidth: 22, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((HelpTopic)row).Kind, MinWidth: 8, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Category", row => ((HelpTopic)row).Category, MinWidth: 8, MaxWidth: 16, Priority: 20),
                    new DisplayTableColumn("Description", row => ((HelpTopic)row).Description, MinWidth: 20, MaxWidth: 64, Priority: 30, CanHide: false),
                    new DisplayTableColumn("Usage", row => ((HelpTopic)row).Usage, MinWidth: 16, MaxWidth: 56, Priority: 40),
                    new DisplayTableColumn("Aliases", row => string.Join(", ", ((HelpTopic)row).Aliases), MinWidth: 6, MaxWidth: 28, Priority: 50),
                    new DisplayTableColumn("Related", row => string.Join(", ", ((HelpTopic)row).Related), MinWidth: 8, MaxWidth: 36, Priority: 60),
                    new DisplayTableColumn("Examples", row => string.Join(" | ", ((HelpTopic)row).Examples), MinWidth: 12, MaxWidth: 72, Priority: 70),
                    new DisplayTableColumn("Path", row => ((HelpTopic)row).Path, MinWidth: 8, MaxWidth: 48, Priority: 80),
                    new DisplayTableColumn("Notes", row => ((HelpTopic)row).Notes, MinWidth: 12, MaxWidth: 72, Priority: 90),
                ]);
    }

    private static DisplayProfile CreateHelpCategoryInfoProfile()
    {
        return DisplayProfile
            .For<HelpCategoryInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Category", row => ((HelpCategoryInfo)row).Category, MinWidth: 10, MaxWidth: 18, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Count", row => ((HelpCategoryInfo)row).Count, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 8, Priority: 10, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateTextStatisticsProfile()
    {
        return DisplayProfile
            .For<TextStatistics>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var stats = (TextStatistics)context.Value;
                    return string.IsNullOrWhiteSpace(stats.Path)
                        ? $"{stats.Lines} lines, {stats.Words} words"
                        : $"{stats.Path}: {stats.Lines} lines, {stats.Words} words";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Path", row => ((TextStatistics)row).Path ?? "<stdin>", MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Lines", row => ((TextStatistics)row).Lines, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 10),
                    new DisplayTableColumn("Words", row => ((TextStatistics)row).Words, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 20),
                    new DisplayTableColumn("Bytes", row => ((TextStatistics)row).Bytes, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 30),
                    new DisplayTableColumn("Chars", row => ((TextStatistics)row).Characters, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 40),
                    new DisplayTableColumn("MaxLine", row => ((TextStatistics)row).LongestLine, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateFormatterStatusProfile()
    {
        return DisplayProfile
            .For<FormatterStatus>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => $"view: {((FormatterStatus)context.Value).Style.ToString().ToLowerInvariant()}");
    }

    private static DisplayProfile CreateXDocumentProfile()
    {
        return DisplayProfile
            .For<XDocument>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var document = (XDocument)context.Value;
                    return document.Root?.ToString(SaveOptions.DisableFormatting) ?? "<empty-xml />";
                });
    }

    private static DisplayProfile CreateXElementProfile()
    {
        return DisplayProfile
            .For<XElement>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((XElement)context.Value).ToString(SaveOptions.DisableFormatting));
    }

    // ── JSON (System.Text.Json) ──────────────────────────────────────────

    private static DisplayProfile CreateJsonDocumentProfile()
    {
        return DisplayProfile
            .For<JsonDocument>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildJsonDocumentColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var doc = (JsonDocument)context.Value;
                    return FormatJsonElementPreview(doc.RootElement);
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonDocumentColumns()
    {
        return
        [
            new DisplayTableColumn("RootKind", row => ((JsonDocument)row).RootElement.ValueKind, MinWidth: 5, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("Content", row => FormatJsonElementPreview(((JsonDocument)row).RootElement), MinWidth: 8, MaxWidth: 96, Priority: 10, CanHide: false),
        ];
    }

    private static DisplayProfile CreateJsonElementProfile()
    {
        return DisplayProfile
            .For<JsonElement>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildJsonElementColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatJsonElementPreview((JsonElement)context.Value));
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonElementColumns()
    {
        return
        [
            new DisplayTableColumn("Kind", row => ((JsonElement)row).ValueKind, MinWidth: 5, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("Value", row => FormatJsonElementValue((JsonElement)row), MinWidth: 4, MaxWidth: 96, Priority: 10, CanHide: false),
        ];
    }

    private static DisplayProfile CreateJsonPropertyProfile()
    {
        return DisplayProfile
            .For<JsonProperty>()
            .AddTableCase(_ => BuildJsonPropertyColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var prop = (JsonProperty)context.Value;
                    return $"{prop.Name}: {FormatJsonElementPreview(prop.Value)}";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonPropertyColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((JsonProperty)row).Name, MinWidth: 4, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Kind", row => ((JsonProperty)row).Value.ValueKind, MinWidth: 5, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("Value", row => FormatJsonElementValue(((JsonProperty)row).Value), MinWidth: 4, MaxWidth: 96, Priority: 20, CanHide: false),
        ];
    }

    private static DisplayProfile CreateJsonNodeProfile()
    {
        return DisplayProfile
            .For<JsonNode>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildJsonNodeColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatJsonNodePreview((JsonNode)context.Value));
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonNodeColumns()
    {
        return
        [
            new DisplayTableColumn("Type", row => ((JsonNode)row).GetValueKind(), MinWidth: 5, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("Value", row => FormatJsonNodePreview((JsonNode)row), MinWidth: 4, MaxWidth: 96, Priority: 10, CanHide: false),
        ];
    }

    private static DisplayProfile CreateJsonObjectProfile()
    {
        return DisplayProfile
            .For<JsonObject>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildJsonObjectColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var obj = (JsonObject)context.Value;
                    return $"{{...}} ({obj.Count} properties)";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonObjectColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((JsonObject)row).Count, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Keys", row => FormatJsonObjectKeys((JsonObject)row), MinWidth: 8, MaxWidth: 96, Priority: 10, CanHide: false),
        ];
    }

    private static DisplayProfile CreateJsonArrayProfile()
    {
        return DisplayProfile
            .For<JsonArray>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildJsonArrayColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var arr = (JsonArray)context.Value;
                    return $"[...] ({arr.Count} items)";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonArrayColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((JsonArray)row).Count, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Preview", row => FormatJsonArrayPreview((JsonArray)row), MinWidth: 8, MaxWidth: 96, Priority: 10, CanHide: false),
        ];
    }

    private static DisplayProfile CreateJsonValueProfile()
    {
        return DisplayProfile
            .For<JsonValue>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildJsonValueColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((JsonValue)context.Value).ToJsonString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildJsonValueColumns()
    {
        return
        [
            new DisplayTableColumn("Kind", row => ((JsonValue)row).GetValueKind(), MinWidth: 5, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("Value", row => ((JsonValue)row).ToJsonString(), MinWidth: 4, MaxWidth: 96, Priority: 10, CanHide: false),
        ];
    }

    private static string FormatJsonObjectKeys(JsonObject obj, int maxKeys = 8)
    {
        var keys = obj.Select(kv => kv.Key).Take(maxKeys + 1).ToList();
        var preview = string.Join(", ", keys.Take(maxKeys));
        return keys.Count > maxKeys ? $"{preview}, ..." : preview;
    }

    private static string FormatJsonArrayPreview(JsonArray arr, int maxItems = 6)
    {
        var items = arr
            .Take(maxItems + 1)
            .Select(n => n is null ? "null" : FormatJsonNodePreview(n))
            .ToList();
        var preview = string.Join(", ", items.Take(maxItems));
        return items.Count > maxItems ? $"[{preview}, ...]" : $"[{preview}]";
    }

    // ── Network information ──────────────────────────────────────────────

    private static IReadOnlyList<DisplayTableColumn> BuildGuidColumns()
    {
        return
        [
            new DisplayTableColumn("Value", row => ((Guid)row).ToString("D", CultureInfo.InvariantCulture), MinWidth: 36, MaxWidth: 36, Priority: 0, CanHide: false),
            new DisplayTableColumn("Version", row => GuidUtilities.GetVersionText((Guid)row), MinWidth: 1, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("Variant", row => GuidUtilities.GetVariantName((Guid)row), MinWidth: 6, MaxWidth: 18, Priority: 20),
            new DisplayTableColumn("Empty", row => ((Guid)row) == Guid.Empty, MinWidth: 4, MaxWidth: 5, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildVersionColumns()
    {
        return
        [
            new DisplayTableColumn("Value", row => ((Version)row).ToString(), MinWidth: 3, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Major", row => ((Version)row).Major, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 10),
            new DisplayTableColumn("Minor", row => ((Version)row).Minor, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 20),
            new DisplayTableColumn("Build", row => FormatVersionComponent(((Version)row).Build), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 30),
            new DisplayTableColumn("Revision", row => FormatVersionComponent(((Version)row).Revision), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 40),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildByteArrayColumns()
    {
        return
        [
            new DisplayTableColumn("Length", row => ((byte[])row).Length, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("Hex", row => FormatByteArrayHexPreview((byte[])row), MinWidth: 2, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("Utf8", row => FormatByteArrayUtf8Preview((byte[])row), MinWidth: 2, MaxWidth: 48, Priority: 20),
            new DisplayTableColumn("Base64", row => FormatByteArrayBase64Preview((byte[])row), MinWidth: 2, MaxWidth: 64, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildUriColumns()
    {
        return
        [
            new DisplayTableColumn("Value", row => new StyledText(((Uri)row).ToString(), Link: ((Uri)row).ToString()), MinWidth: 12, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Scheme", row => ((Uri)row).Scheme, MinWidth: 3, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("Host", row => ((Uri)row).IsAbsoluteUri ? ((Uri)row).Host : null, MinWidth: 4, MaxWidth: 48, Priority: 20),
            new DisplayTableColumn("Port", row => ((Uri)row).IsAbsoluteUri && !((Uri)row).IsDefaultPort ? ((Uri)row).Port : null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 30),
            new DisplayTableColumn("Path", row => ((Uri)row).IsAbsoluteUri ? ((Uri)row).AbsolutePath : ((Uri)row).OriginalString, MinWidth: 1, MaxWidth: 72, Priority: 40),
            new DisplayTableColumn("Query", row => ((Uri)row).IsAbsoluteUri ? NullIfEmpty(((Uri)row).Query) : null, MinWidth: 1, MaxWidth: 72, Priority: 50),
            new DisplayTableColumn("Fragment", row => ((Uri)row).IsAbsoluteUri ? NullIfEmpty(((Uri)row).Fragment) : null, MinWidth: 1, MaxWidth: 48, Priority: 60),
            new DisplayTableColumn("Absolute", row => ((Uri)row).IsAbsoluteUri, MinWidth: 4, MaxWidth: 5, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildRegexColumns()
    {
        return
        [
            new DisplayTableColumn("Pattern", row => ((Regex)row).ToString(), MinWidth: 4, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Options", row => ((Regex)row).Options, MinWidth: 4, MaxWidth: 32, Priority: 10),
            new DisplayTableColumn("Timeout", row => FormatRegexTimeout(((Regex)row).MatchTimeout), MinWidth: 4, MaxWidth: 24, Priority: 20),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTimeZoneInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Id", row => ((TimeZoneInfo)row).Id, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("DisplayName", row => ((TimeZoneInfo)row).DisplayName, MinWidth: 8, MaxWidth: 72, Priority: 10),
            new DisplayTableColumn("BaseUtcOffset", row => FormatUtcOffset(((TimeZoneInfo)row).BaseUtcOffset), MinWidth: 6, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("SupportsDst", row => ((TimeZoneInfo)row).SupportsDaylightSavingTime, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("StandardName", row => ((TimeZoneInfo)row).StandardName, MinWidth: 4, MaxWidth: 48, Priority: 40),
            new DisplayTableColumn("DaylightName", row => ((TimeZoneInfo)row).DaylightName, MinWidth: 4, MaxWidth: 48, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCultureInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => FormatCultureName((CultureInfo)row), MinWidth: 3, MaxWidth: 16, Priority: 0, CanHide: false),
            new DisplayTableColumn("DisplayName", row => ((CultureInfo)row).DisplayName, MinWidth: 8, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("EnglishName", row => ((CultureInfo)row).EnglishName, MinWidth: 8, MaxWidth: 48, Priority: 20),
            new DisplayTableColumn("NativeName", row => ((CultureInfo)row).NativeName, MinWidth: 8, MaxWidth: 48, Priority: 30),
            new DisplayTableColumn("Iso2", row => ((CultureInfo)row).TwoLetterISOLanguageName, MinWidth: 2, MaxWidth: 8, Priority: 40),
            new DisplayTableColumn("Neutral", row => ((CultureInfo)row).IsNeutralCulture, MinWidth: 4, MaxWidth: 5, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildEncodingColumns()
    {
        return
        [
            new DisplayTableColumn("WebName", row => ((Encoding)row).WebName, MinWidth: 3, MaxWidth: 18, Priority: 0, CanHide: false),
            new DisplayTableColumn("EncodingName", row => ((Encoding)row).EncodingName, MinWidth: 8, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("CodePage", row => ((Encoding)row).CodePage, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("SingleByte", row => ((Encoding)row).IsSingleByte, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("Preamble", row => FormatEncodingPreamble((Encoding)row), MinWidth: 2, MaxWidth: 24, Priority: 40),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildExceptionColumns()
    {
        return
        [
            new DisplayTableColumn("Message", row => ((Exception)row).Message, MinWidth: 8, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Source", row => ((Exception)row).Source, MinWidth: 2, MaxWidth: 32, Priority: 10),
            new DisplayTableColumn("HResult", row => FormatExceptionHResult((Exception)row), MinWidth: 8, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("Inner", row => ((Exception)row).InnerException?.GetType().Name, MinWidth: 2, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("HelpLink", row => ((Exception)row).HelpLink, MinWidth: 2, MaxWidth: 72, Priority: 40),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildKeyValuePairColumns()
    {
        return
        [
            new DisplayTableColumn("Key", row => GetKeyValuePairComponent(row, "Key"), MinWidth: 3, MaxWidth: 64, Priority: 0, CanHide: false),
            new DisplayTableColumn("Value", row => GetKeyValuePairComponent(row, "Value"), MinWidth: 3, MaxWidth: 96, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTupleColumns(IReadOnlyList<object> rows)
    {
        var itemCount = rows.Count == 0
            ? 0
            : rows.OfType<ITuple>().Max(tuple => tuple.Length);

        if (itemCount <= 0)
        {
            return
            [
                new DisplayTableColumn("Value", _ => "()", MinWidth: 2, MaxWidth: 4, Priority: 0, CanHide: false),
            ];
        }

        var columns = new List<DisplayTableColumn>(itemCount);

        for (var index = 0; index < itemCount; index++)
        {
            var currentIndex = index;
            columns.Add(new DisplayTableColumn(
                $"Item{index + 1}",
                row => GetTupleItem((ITuple)row, currentIndex),
                MinWidth: 3,
                MaxWidth: 48,
                Priority: index * 10,
                CanHide: index > 0));
        }

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHashSetColumns()
    {
        return
        [
            new DisplayTableColumn(
                "Count",
                row => row is ICollection collection ? collection.Count : null,
                MinWidth: 3,
                MaxWidth: 8,
                Priority: 0,
                CanHide: false),
            new DisplayTableColumn(
                "Preview",
                row => FormatHashSetPreview(row),
                MinWidth: 6,
                MaxWidth: 96,
                Priority: 10,
                CanHide: true),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIndexColumns()
    {
        return
        [
            new DisplayTableColumn("Value", row => FormatIndexValue((Index)row), MinWidth: 1, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("RawValue", row => ((Index)row).Value, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("FromEnd", row => ((Index)row).IsFromEnd, MinWidth: 4, MaxWidth: 5, Priority: 20),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildRangeColumns()
    {
        return
        [
            new DisplayTableColumn("Value", row => FormatRangeValue((Range)row), MinWidth: 3, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Start", row => FormatIndexValue(((Range)row).Start), MinWidth: 1, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("End", row => FormatIndexValue(((Range)row).End), MinWidth: 1, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("StartFromEnd", row => ((Range)row).Start.IsFromEnd, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("EndFromEnd", row => ((Range)row).End.IsFromEnd, MinWidth: 4, MaxWidth: 5, Priority: 40),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildMethodBaseColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((MethodBase)row).Name, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("DeclaringType", row => GetReadableTypeName(((MethodBase)row).DeclaringType), MinWidth: 4, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("Static", row => ((MethodBase)row).IsStatic, MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("ReturnType", row => GetMethodBaseReturnType((MethodBase)row), MinWidth: 4, MaxWidth: 64, Priority: 30),
            new DisplayTableColumn("ParameterCount", row => ((MethodBase)row).GetParameters().Length, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 40),
            new DisplayTableColumn("Signature", row => FormatMethodBaseSummary((MethodBase)row), MinWidth: 8, MaxWidth: 128, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildPropertyInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((PropertyInfo)row).Name, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("DeclaringType", row => GetReadableTypeName(((PropertyInfo)row).DeclaringType), MinWidth: 4, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("PropertyType", row => GetReadableTypeName(((PropertyInfo)row).PropertyType), MinWidth: 4, MaxWidth: 64, Priority: 20),
            new DisplayTableColumn("Static", row => IsPropertyStatic((PropertyInfo)row), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("Readable", row => ((PropertyInfo)row).CanRead, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("Writable", row => ((PropertyInfo)row).CanWrite, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("Indexer", row => ((PropertyInfo)row).GetIndexParameters().Length > 0, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Signature", row => FormatPropertyInfoSummary((PropertyInfo)row), MinWidth: 8, MaxWidth: 128, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildStackFrameColumns()
    {
        return
        [
            new DisplayTableColumn("Method", row => GetStackFrameMethodName((StackFrame)row), MinWidth: 4, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("DeclaringType", row => GetStackFrameDeclaringType((StackFrame)row), MinWidth: 4, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("File", row => NullIfEmpty(((StackFrame)row).GetFileName()), MinWidth: 4, MaxWidth: 96, Priority: 20),
            new DisplayTableColumn("Line", row => NullIfZero(((StackFrame)row).GetFileLineNumber()), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 30),
            new DisplayTableColumn("Column", row => NullIfZero(((StackFrame)row).GetFileColumnNumber()), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 40),
            new DisplayTableColumn("ILOffset", row => NullIfNegative(((StackFrame)row).GetILOffset()), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 50),
            new DisplayTableColumn("NativeOffset", row => NullIfNegative(((StackFrame)row).GetNativeOffset()), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 60),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildStackTraceColumns()
    {
        return
        [
            new DisplayTableColumn("FrameCount", row => GetStackFrameCount((StackTrace)row), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Frames", row => FormatStackTraceFrames((StackTrace)row), MinWidth: 8, MaxWidth: 160, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildDictionaryEntryColumns()
    {
        return
        [
            new DisplayTableColumn("Key", row => ((DictionaryEntry)row).Key, MinWidth: 3, MaxWidth: 64, Priority: 0, CanHide: false),
            new DisplayTableColumn("Value", row => ((DictionaryEntry)row).Value, MinWidth: 3, MaxWidth: 96, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildAssemblyNameColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((AssemblyName)row).Name, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Version", row => ((AssemblyName)row).Version?.ToString(), MinWidth: 3, MaxWidth: 24, Priority: 10),
            new DisplayTableColumn("Culture", row => NullIfEmpty(((AssemblyName)row).CultureName) ?? "<neutral>", MinWidth: 3, MaxWidth: 24, Priority: 20),
            new DisplayTableColumn("PublicKeyToken", row => FormatPublicKeyToken((AssemblyName)row), MinWidth: 6, MaxWidth: 32, Priority: 30),
            new DisplayTableColumn("Flags", row => ((AssemblyName)row).Flags, MinWidth: 3, MaxWidth: 48, Priority: 40),
            new DisplayTableColumn("FullName", row => ((AssemblyName)row).FullName, MinWidth: 8, MaxWidth: 160, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTypeColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((Type)row).Name, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("FullName", row => ReflectionMetadataUtilities.GetDisplayName((Type)row), MinWidth: 6, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("Namespace", row => ((Type)row).Namespace, MinWidth: 3, MaxWidth: 64, Priority: 20),
            new DisplayTableColumn("Assembly", row => ((Type)row).Assembly.GetName().Name, MinWidth: 3, MaxWidth: 64, Priority: 30),
            new DisplayTableColumn("BaseType", row => GetReadableTypeName(((Type)row).BaseType), MinWidth: 3, MaxWidth: 96, Priority: 40),
            new DisplayTableColumn("IsClass", row => ((Type)row).IsClass, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("IsInterface", row => ((Type)row).IsInterface, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("IsEnum", row => ((Type)row).IsEnum, MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("IsValueType", row => ((Type)row).IsValueType, MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("IsAbstract", row => ((Type)row).IsAbstract, MinWidth: 4, MaxWidth: 5, Priority: 90),
            new DisplayTableColumn("IsGenericType", row => ((Type)row).IsGenericType, MinWidth: 4, MaxWidth: 5, Priority: 100),
            new DisplayTableColumn("IsArray", row => ((Type)row).IsArray, MinWidth: 4, MaxWidth: 5, Priority: 110),
            new DisplayTableColumn("IsPublic", row => ((Type)row).IsPublic || ((Type)row).IsNestedPublic, MinWidth: 4, MaxWidth: 5, Priority: 120),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildAssemblyColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((Assembly)row).GetName().Name, MinWidth: 3, MaxWidth: 64, Priority: 0, CanHide: false),
            new DisplayTableColumn("Version", row => ((Assembly)row).GetName().Version?.ToString(), MinWidth: 3, MaxWidth: 24, Priority: 10),
            new DisplayTableColumn("Culture", row => NullIfEmpty(((Assembly)row).GetName().CultureName) ?? "<neutral>", MinWidth: 3, MaxWidth: 24, Priority: 20),
            new DisplayTableColumn("Location", row => SafeGetAssemblyLocation((Assembly)row), MinWidth: 3, MaxWidth: 128, Priority: 30),
            new DisplayTableColumn("ImageRuntime", row => ((Assembly)row).ImageRuntimeVersion, MinWidth: 3, MaxWidth: 16, Priority: 40),
            new DisplayTableColumn("Dynamic", row => ((Assembly)row).IsDynamic, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("EntryPoint", row => ((Assembly)row).EntryPoint?.Name, MinWidth: 3, MaxWidth: 64, Priority: 60),
            new DisplayTableColumn("DefinedTypes", row => SafeGetDefinedTypeCount((Assembly)row), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFieldInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((FieldInfo)row).Name, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("DeclaringType", row => GetReadableTypeName(((FieldInfo)row).DeclaringType), MinWidth: 4, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("FieldType", row => GetReadableTypeName(((FieldInfo)row).FieldType), MinWidth: 4, MaxWidth: 64, Priority: 20),
            new DisplayTableColumn("Static", row => ((FieldInfo)row).IsStatic, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("InitOnly", row => ((FieldInfo)row).IsInitOnly, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("Literal", row => ((FieldInfo)row).IsLiteral, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("Public", row => ((FieldInfo)row).IsPublic, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Signature", row => FormatFieldInfoSummary((FieldInfo)row), MinWidth: 8, MaxWidth: 128, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildEventInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((EventInfo)row).Name, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("DeclaringType", row => GetReadableTypeName(((EventInfo)row).DeclaringType), MinWidth: 4, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("HandlerType", row => GetReadableTypeName(((EventInfo)row).EventHandlerType), MinWidth: 4, MaxWidth: 64, Priority: 20),
            new DisplayTableColumn("Static", row => IsEventStatic((EventInfo)row), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("Multicast", row => IsMulticastEvent((EventInfo)row), MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("AddMethod", row => ((EventInfo)row).AddMethod?.Name, MinWidth: 3, MaxWidth: 48, Priority: 50),
            new DisplayTableColumn("RemoveMethod", row => ((EventInfo)row).RemoveMethod?.Name, MinWidth: 3, MaxWidth: 48, Priority: 60),
            new DisplayTableColumn("Signature", row => FormatEventInfoSummary((EventInfo)row), MinWidth: 8, MaxWidth: 128, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildParameterInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((ParameterInfo)row).Name, MinWidth: 3, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("ParameterType", row => GetReadableTypeName(((ParameterInfo)row).ParameterType), MinWidth: 4, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("Position", row => ((ParameterInfo)row).Position, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("Optional", row => ((ParameterInfo)row).IsOptional, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("HasDefault", row => ((ParameterInfo)row).HasDefaultValue, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("DefaultValue", row => FormatParameterDefaultValue((ParameterInfo)row), MinWidth: 3, MaxWidth: 48, Priority: 50),
            new DisplayTableColumn("Out", row => ((ParameterInfo)row).IsOut, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("In", row => ((ParameterInfo)row).IsIn, MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("Signature", row => FormatParameterInfoSummary((ParameterInfo)row), MinWidth: 8, MaxWidth: 128, Priority: 80),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildAssemblyLoadContextColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => NullIfEmpty(((AssemblyLoadContext)row).Name) ?? "<anonymous>", MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("IsDefault", row => ReferenceEquals((AssemblyLoadContext)row, AssemblyLoadContext.Default), MinWidth: 4, MaxWidth: 5, Priority: 10),
            new DisplayTableColumn("IsCollectible", row => ((AssemblyLoadContext)row).IsCollectible, MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("LoadedAssemblies", row => GetAssemblyLoadContextAssemblyCount((AssemblyLoadContext)row), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 30),
            new DisplayTableColumn("Assemblies", row => FormatAssemblyLoadContextAssemblies((AssemblyLoadContext)row), MinWidth: 8, MaxWidth: 160, Priority: 40),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileVersionInfoColumns()
    {
        return
        [
            new DisplayTableColumn("FileName", row => NullIfEmpty(((FileVersionInfo)row).FileName), MinWidth: 3, MaxWidth: 128, Priority: 0, CanHide: false),
            new DisplayTableColumn("FileDescription", row => NullIfEmpty(((FileVersionInfo)row).FileDescription), MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("FileVersion", row => NullIfEmpty(((FileVersionInfo)row).FileVersion), MinWidth: 3, MaxWidth: 48, Priority: 20),
            new DisplayTableColumn("ProductName", row => NullIfEmpty(((FileVersionInfo)row).ProductName), MinWidth: 3, MaxWidth: 64, Priority: 30),
            new DisplayTableColumn("ProductVersion", row => NullIfEmpty(((FileVersionInfo)row).ProductVersion), MinWidth: 3, MaxWidth: 48, Priority: 40),
            new DisplayTableColumn("CompanyName", row => NullIfEmpty(((FileVersionInfo)row).CompanyName), MinWidth: 3, MaxWidth: 64, Priority: 50),
            new DisplayTableColumn("Language", row => NullIfEmpty(((FileVersionInfo)row).Language), MinWidth: 3, MaxWidth: 32, Priority: 60),
            new DisplayTableColumn("OriginalFilename", row => NullIfEmpty(((FileVersionInfo)row).OriginalFilename), MinWidth: 3, MaxWidth: 96, Priority: 70),
            new DisplayTableColumn("InternalName", row => NullIfEmpty(((FileVersionInfo)row).InternalName), MinWidth: 3, MaxWidth: 64, Priority: 80),
            new DisplayTableColumn("Comments", row => NullIfEmpty(((FileVersionInfo)row).Comments), MinWidth: 3, MaxWidth: 96, Priority: 90),
            new DisplayTableColumn("Debug", row => ((FileVersionInfo)row).IsDebug, MinWidth: 4, MaxWidth: 5, Priority: 100),
            new DisplayTableColumn("PreRelease", row => ((FileVersionInfo)row).IsPreRelease, MinWidth: 4, MaxWidth: 5, Priority: 110),
            new DisplayTableColumn("Patched", row => ((FileVersionInfo)row).IsPatched, MinWidth: 4, MaxWidth: 5, Priority: 120),
            new DisplayTableColumn("PrivateBuild", row => ((FileVersionInfo)row).IsPrivateBuild, MinWidth: 4, MaxWidth: 5, Priority: 130),
            new DisplayTableColumn("SpecialBuild", row => ((FileVersionInfo)row).IsSpecialBuild, MinWidth: 4, MaxWidth: 5, Priority: 140),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildColumnSummaryColumns(IReadOnlyList<object> rows)
    {
        var summaries = rows.Cast<ColumnSummary>().ToArray();
        var columns = new List<DisplayTableColumn>
        {
            new("Column", row => ((ColumnSummary)row).Column, MinWidth: 6, MaxWidth: 24, Priority: 0, CanHide: false, SelectionKey: "COLUMN"),
            new("Rows", row => ((ColumnSummary)row).RowCount, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 10, SelectionKey: "ROWS"),
            new("Values", row => ((ColumnSummary)row).ValueCount, DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 8, Priority: 20, SelectionKey: "VALUES"),
        };

        if (summaries.Any(summary => summary.Count is not null))
        {
            columns.Add(new DisplayTableColumn("Count", row => ((ColumnSummary)row).Count, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 12, Priority: 30, SelectionKey: "COUNT"));
        }

        if (summaries.Any(summary => summary.Sum is not null))
        {
            columns.Add(new DisplayTableColumn("Sum", row => ((ColumnSummary)row).Sum, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 20, Priority: 40, SelectionKey: "SUM"));
        }

        if (summaries.Any(summary => summary.Average is not null))
        {
            columns.Add(new DisplayTableColumn("Average", row => ((ColumnSummary)row).Average, DisplayTableAlignment.Right, MinWidth: 7, MaxWidth: 20, Priority: 50, SelectionKey: "AVERAGE"));
        }

        if (summaries.Any(summary => summary.Min is not null))
        {
            columns.Add(new DisplayTableColumn("Min", row => ((ColumnSummary)row).Min, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 20, Priority: 60, SelectionKey: "MIN"));
        }

        if (summaries.Any(summary => summary.Max is not null))
        {
            columns.Add(new DisplayTableColumn("Max", row => ((ColumnSummary)row).Max, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 20, Priority: 70, SelectionKey: "MAX"));
        }

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildColumnSummarySelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Column", row => ((ColumnSummary)row).Column, MinWidth: 6, MaxWidth: 24, Priority: 0, CanHide: false, SelectionKey: "COLUMN"),
            new DisplayTableColumn("Rows", row => ((ColumnSummary)row).RowCount, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 10, SelectionKey: "ROWS"),
            new DisplayTableColumn("Values", row => ((ColumnSummary)row).ValueCount, DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 8, Priority: 20, SelectionKey: "VALUES"),
            new DisplayTableColumn("Count", row => ((ColumnSummary)row).Count, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 12, Priority: 30, SelectionKey: "COUNT"),
            new DisplayTableColumn("Sum", row => ((ColumnSummary)row).Sum, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 20, Priority: 40, SelectionKey: "SUM"),
            new DisplayTableColumn("Average", row => ((ColumnSummary)row).Average, DisplayTableAlignment.Right, MinWidth: 7, MaxWidth: 20, Priority: 50, SelectionKey: "AVERAGE"),
            new DisplayTableColumn("Min", row => ((ColumnSummary)row).Min, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 20, Priority: 60, SelectionKey: "MIN"),
            new DisplayTableColumn("Max", row => ((ColumnSummary)row).Max, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 20, Priority: 70, SelectionKey: "MAX"),
        ];
    }

    private static string FormatDateOnly(
        DateOnly value,
        DateOnlyDisplayMode mode,
        string? format,
        Func<DateTimeOffset> nowProvider)
    {
        return mode switch
        {
            DateOnlyDisplayMode.Iso => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnlyDisplayMode.Long => value.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture),
            DateOnlyDisplayMode.Relative => FormatRelativeDate(value, nowProvider()),
            DateOnlyDisplayMode.Custom => value.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatTimeOnly(TimeOnly value, TimeOnlyDisplayMode mode, string? format)
    {
        return mode switch
        {
            TimeOnlyDisplayMode.TwentyFourHour => value.ToString(GetTimeOnlyFormat(value, useTwentyFourHour: true), CultureInfo.InvariantCulture),
            TimeOnlyDisplayMode.TwelveHour => value.ToString(GetTimeOnlyFormat(value, useTwentyFourHour: false), CultureInfo.InvariantCulture),
            TimeOnlyDisplayMode.Custom => value.ToString(format ?? GetTimeOnlyFormat(value, useTwentyFourHour: true), CultureInfo.InvariantCulture),
            _ => value.ToString(GetTimeOnlyFormat(value, useTwentyFourHour: true), CultureInfo.InvariantCulture),
        };
    }

    private static object? FormatVersionComponent(int value)
    {
        return value >= 0 ? value : null;
    }

    private static string FormatByteArrayPreview(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "0 bytes";
        }

        return $"{bytes.Length} byte{(bytes.Length == 1 ? string.Empty : "s")} [ {FormatByteArrayHexPreview(bytes, 8)} ]";
    }

    private static string FormatByteArrayUtf8Preview(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var preview = Encoding.UTF8.GetString(bytes);
            if (preview.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
            {
                return "<binary>";
            }

            preview = preview.Replace("\r", "\\r", StringComparison.Ordinal)
                             .Replace("\n", "\\n", StringComparison.Ordinal)
                             .Replace("\t", "\\t", StringComparison.Ordinal);
            return preview.Length > 48 ? preview[..48] + "…" : preview;
        }
        catch
        {
            return "<binary>";
        }
    }

    private static string FormatByteArrayBase64Preview(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var text = Convert.ToBase64String(bytes);
        return text.Length > 64 ? text[..64] + "…" : text;
    }

    private static string FormatRegexTimeout(TimeSpan timeout)
    {
        return timeout == Regex.InfiniteMatchTimeout
            ? "infinite"
            : FormatLongDuration(timeout);
    }

    private static string FormatUtcOffset(TimeSpan value)
    {
        var sign = value < TimeSpan.Zero ? "-" : "+";
        var absolute = value.Duration();
        return $"{sign}{absolute:hh\\:mm}";
    }

    private static string FormatCultureName(CultureInfo value)
    {
        return string.IsNullOrWhiteSpace(value.Name) ? "<invariant>" : value.Name;
    }

    private static string FormatEncodingPreamble(Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        return preamble.Length == 0 ? "<none>" : FormatByteArrayHexPreview(preamble, 8);
    }

    private static string FormatExceptionHResult(Exception exception)
    {
        return $"0x{exception.HResult:X8}";
    }

    private static string FormatKeyValuePairSummary(object row)
    {
        var key = FormatDisplaySummaryValue(GetKeyValuePairComponent(row, "Key"));
        var value = FormatDisplaySummaryValue(GetKeyValuePairComponent(row, "Value"));
        return $"{key} => {value}";
    }

    private static object? GetTupleItem(ITuple tuple, int index)
    {
        return index >= 0 && index < tuple.Length ? tuple[index] : null;
    }

    private static string FormatAssemblyLoadContextAssemblies(AssemblyLoadContext loadContext, int maxAssemblies = 12)
    {
        var names = loadContext.Assemblies
            .Select(assembly => assembly.GetName().Name ?? assembly.GetName().FullName ?? "<unknown>")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(maxAssemblies + 1)
            .ToList();

        if (names.Count == 0)
        {
            return "<none>";
        }

        if (names.Count > maxAssemblies)
        {
            names = names.Take(maxAssemblies).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, names);
    }

    private static string FormatPublicKeyToken(AssemblyName assemblyName)
    {
        var token = assemblyName.GetPublicKeyToken();

        if (token is null || token.Length == 0)
        {
            return "<none>";
        }

        return string.Concat(token.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string? SafeGetAssemblyLocation(Assembly assembly)
    {
        try
        {
#pragma warning disable IL3000 // Assembly.Location returns empty in single-file; NullIfEmpty handles that.
            return NullIfEmpty(assembly.Location);
#pragma warning restore IL3000
        }
        catch
        {
            return null;
        }
    }

    private static object? SafeGetDefinedTypeCount(Assembly assembly)
    {
        try
        {
            return assembly.DefinedTypes.Count();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEventStatic(EventInfo eventInfo)
    {
        return (eventInfo.AddMethod ?? eventInfo.RemoveMethod)?.IsStatic ?? false;
    }

    private static bool IsMulticastEvent(EventInfo eventInfo)
    {
        return eventInfo.EventHandlerType is not null &&
               typeof(MulticastDelegate).IsAssignableFrom(eventInfo.EventHandlerType);
    }

    private static object? FormatParameterDefaultValue(ParameterInfo parameter)
    {
        return parameter.HasDefaultValue ? parameter.DefaultValue : null;
    }

    private static string? GetMethodBaseReturnType(MethodBase methodBase)
    {
        return methodBase is MethodInfo methodInfo
            ? GetReadableTypeName(methodInfo.ReturnType)
            : null;
    }

    private static bool IsPropertyStatic(PropertyInfo property)
    {
        return (property.GetMethod ?? property.SetMethod)?.IsStatic ?? false;
    }

    private static string? GetStackFrameMethodName(StackFrame frame)
    {
        return frame.GetMethod() is { } method ? FormatMethodBaseSummary(method) : null;
    }

    private static string? GetStackFrameDeclaringType(StackFrame frame)
    {
        return GetReadableTypeName(frame.GetMethod()?.DeclaringType);
    }

    private static string FormatStackTraceFrames(StackTrace trace, int maxFrames = 12)
    {
        var frames = trace.GetFrames() ?? [];

        if (frames.Length == 0)
        {
            return "<empty>";
        }

        var lines = frames
            .Take(maxFrames)
            .Select(FormatStackFrameSummary)
            .ToList();

        if (frames.Length > maxFrames)
        {
            lines.Add($"… (+{(frames.Length - maxFrames).ToString(CultureInfo.InvariantCulture)} more)");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static object? NullIfZero(int value)
    {
        return value == 0 ? null : value;
    }

    private static object? NullIfNegative(int value)
    {
        return value < 0 ? null : value;
    }

    private static string FormatRelativeDate(DateOnly value, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var deltaDays = value.DayNumber - today.DayNumber;

        return deltaDays switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            > 0 => $"in {deltaDays} day{(deltaDays == 1 ? string.Empty : "s")}",
            _ => $"{-deltaDays} day{(deltaDays == -1 ? string.Empty : "s")} ago",
        };
    }

    private static string GetTimeOnlyFormat(TimeOnly value, bool useTwentyFourHour)
    {
        var hasSubSecond = value.Ticks % TimeSpan.TicksPerSecond != 0;

        return (useTwentyFourHour, hasSubSecond) switch
        {
            (true, true) => "HH:mm:ss.fffffff",
            (true, false) => "HH:mm:ss",
            (false, true) => "h:mm:ss.fffffff tt",
            _ => "h:mm:ss tt",
        };
    }

    private static string FormatDateTime(
        DateTime value,
        TemporalDisplayMode mode,
        string? format,
        Func<DateTimeOffset> nowProvider)
    {
        return mode switch
        {
            TemporalDisplayMode.Iso => value.ToString("O", CultureInfo.InvariantCulture),
            // Through `ToDisplayInstant` rather than `value.ToLocalTime()` — `TOSH-0006`.
            // .NET's `ToLocalTime` assumes an `Unspecified` kind means UTC and converts from
            // it, so a value written `12:00` displayed as `08:00`. `ToDisplayInstant` reads
            // `Unspecified` as local, which is what a wall-clock literal means, and is what
            // the `Relative` and `Unix` modes below already use: one mode was disagreeing
            // with the other three about the same question.
            TemporalDisplayMode.Local => ToDisplayInstant(value).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Relative => FormatRelativeTime(ToDisplayInstant(value), nowProvider()),
            TemporalDisplayMode.Unix => ToDisplayInstant(value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            TemporalDisplayMode.Custom => value.ToString(format ?? "O", CultureInfo.InvariantCulture),
            _ => value.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static DateTimeOffset ToDisplayInstant(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
        }

        return new DateTimeOffset(value);
    }

    private static string FormatTemporalAmount(TemporalAmount value, DurationDisplayMode mode, string? format)
    {
        if (value.TryAsTimeSpan(out var duration))
        {
            return FormatTimeSpan(duration, mode, format);
        }

        return mode switch
        {
            DurationDisplayMode.Long => FormatLongTemporalAmount(value),
            _ => value.ToString(),
        };
    }

    private static string FormatLongTemporalAmount(TemporalAmount value)
    {
        if (value.Months == 0 && value.Duration == TimeSpan.Zero)
        {
            return "0 seconds";
        }

        var parts = new List<string>();

        if (value.Months != 0)
        {
            AppendCalendarParts(parts, value.Months);
        }

        if (value.Duration != TimeSpan.Zero)
        {
            parts.Add(FormatLongDuration(value.Duration));
        }

        return string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void AppendCalendarParts(List<string> parts, long months)
    {
        var negative = months < 0;
        var remaining = Math.Abs(months);
        var years = remaining / 12;
        remaining %= 12;

        if (years > 0)
        {
            var text = years == 1 ? "1 year" : $"{years} years";
            parts.Add(negative ? "-" + text : text);
            negative = false;
        }

        if (remaining > 0)
        {
            var text = remaining == 1 ? "1 month" : $"{remaining} months";
            parts.Add(negative ? "-" + text : text);
        }
    }

    private static string FormatColor(Color color)
    {
        if (color.IsEmpty)
        {
            return "<empty color>";
        }

        return StyledText.RenderSegments(
        [
            new StyledText("■", Foreground: FormatColorHex(color)),
            " ",
            FormatColorName(color),
            " (",
            FormatColorHex(color),
            ")",
        ]);
    }

    private static string FormatColorCell(Color color)
    {
        return StyledText.RenderSegments(
        [
            new StyledText("■", Foreground: FormatColorHex(color)),
            " ",
            FormatColorName(color),
        ]);
    }

    private static DisplayProfile CreateStyledTextProfile()
    {
        return DisplayProfile
            .For<StyledText>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((StyledText)context.Value).ToAnsi());
    }

    private static DisplayProfile CreateX509Certificate2Profile()
    {
        return DisplayProfile
            .For<X509Certificate2>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildX509Certificate2Columns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var cert = (X509Certificate2)context.Value;
                    var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                    return $"{cn} (expires {cert.NotAfter:yyyy-MM-dd})";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildX509Certificate2Columns()
    {
        return
        [
            new DisplayTableColumn("Subject", row => ((X509Certificate2)row).GetNameInfo(X509NameType.SimpleName, false), MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Issuer", row => ((X509Certificate2)row).GetNameInfo(X509NameType.SimpleName, true), MinWidth: 8, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("Thumbprint", row => ((X509Certificate2)row).Thumbprint, MinWidth: 10, MaxWidth: 40, Priority: 30),
            new DisplayTableColumn("NotBefore", row => ((X509Certificate2)row).NotBefore, MinWidth: 10, MaxWidth: 20, Priority: 40),
            new DisplayTableColumn("NotAfter", row => ((X509Certificate2)row).NotAfter, MinWidth: 10, MaxWidth: 20, Priority: 20),
            new DisplayTableColumn("HasPrivateKey", row => ((X509Certificate2)row).HasPrivateKey, MinWidth: 5, MaxWidth: 5, Priority: 50),
        ];
    }

    private static DisplayProfile CreateX500DistinguishedNameProfile()
    {
        return DisplayProfile
            .For<X500DistinguishedName>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((X500DistinguishedName)context.Value).Name);
    }

    private static DisplayProfile CreateOidProfile()
    {
        return DisplayProfile
            .For<Oid>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var oid = (Oid)context.Value;
                    return string.IsNullOrEmpty(oid.FriendlyName) ? oid.Value ?? "" : $"{oid.FriendlyName} ({oid.Value})";
                });
    }

    private static DisplayProfile CreateClaimProfile()
    {
        return DisplayProfile
            .For<Claim>()
            .AddTableCase(_ =>
            [
                new DisplayTableColumn("Type", row => FormatClaimType(((Claim)row).Type), MinWidth: 6, MaxWidth: 32, Priority: 0, CanHide: false),
                new DisplayTableColumn("Value", row => ((Claim)row).Value, MinWidth: 6, MaxWidth: 48, Priority: 10, CanHide: false),
                new DisplayTableColumn("Issuer", row => ((Claim)row).Issuer, MinWidth: 4, MaxWidth: 24, Priority: 20),
            ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var claim = (Claim)context.Value;
                    return $"{FormatClaimType(claim.Type)}: {claim.Value}";
                });
    }

    private static string FormatClaimType(string claimType)
    {
        // Shorten well-known claim URIs to just the final segment
        var lastSlash = claimType.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < claimType.Length - 1 ? claimType[(lastSlash + 1)..] : claimType;
    }

    private static DisplayProfile CreateClaimsIdentityProfile()
    {
        return DisplayProfile
            .For<ClaimsIdentity>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ClaimsIdentity)row).Name, MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("AuthType", row => ((ClaimsIdentity)row).AuthenticationType, MinWidth: 4, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("Authenticated", row => ((ClaimsIdentity)row).IsAuthenticated, MinWidth: 5, MaxWidth: 5, Priority: 20),
                    new DisplayTableColumn("Claims", row => ((ClaimsIdentity)row).Claims.Count(), DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 30),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var id = (ClaimsIdentity)context.Value;
                    var name = id.Name ?? "<anonymous>";
                    return id.IsAuthenticated ? $"{name} ({id.AuthenticationType})" : $"{name} (unauthenticated)";
                });
    }

    private static DisplayProfile CreateClaimsPrincipalProfile()
    {
        return DisplayProfile
            .For<ClaimsPrincipal>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Identity", row => ((ClaimsPrincipal)row).Identity?.Name ?? "<none>", MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Identities", row => ((ClaimsPrincipal)row).Identities.Count(), DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 10),
                    new DisplayTableColumn("Claims", row => ((ClaimsPrincipal)row).Claims.Count(), DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 20),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var principal = (ClaimsPrincipal)context.Value;
                    var name = principal.Identity?.Name ?? "<anonymous>";
                    var count = principal.Identities.Count();
                    return count > 1 ? $"{name} ({count} identities)" : name;
                });
    }

    // ── Numerics and Geometry ────────────────────────────────────────────


}
