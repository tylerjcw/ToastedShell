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

public static class BuiltInDisplayProfiles
{
    public static void RegisterDefaults(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(preferences);

        registry.Register(CreateDateTimeProfile(preferences));
        registry.Register(CreateDateTimeOffsetProfile(preferences));
        registry.Register(CreateDateOnlyProfile(preferences));
        registry.Register(CreateTimeOnlyProfile(preferences));
        registry.Register(CreateTimeSpanProfile(preferences));
        registry.Register(CreateTemporalAmountProfile(preferences));
        registry.Register(CreateStorageSizeProfile(preferences));
        registry.Register(CreateShellTextLineProfile());
        registry.Register(CreateManagedFileHandleProfile());
        registry.Register(CreateIpAddressProfile());
        registry.Register(CreateCommandTimingInfoProfile());
        registry.Register(CreateIpAddressInfoProfile());
        registry.Register(CreateIpInterfaceProfile());
        registry.Register(CreateIpRouteProfile());
        registry.Register(CreateIpNeighborProfile());
        registry.Register(CreateIpRuleProfile());
        registry.Register(CreateIpNetnsProfile());
        registry.Register(CreateIpTunnelProfile());
        registry.Register(CreateIpTuntapProfile());
        registry.Register(CreateIpVrfProfile());
        registry.Register(CreateIpMaddrProfile());
        registry.Register(CreateIpMaddrEntryProfile());
        registry.Register(CreateIpMrouteProfile());
        registry.Register(CreateIpTokenProfile());
        registry.Register(CreateIpNtableProfile());
        registry.Register(CreateSystemdUnitInfoProfile());
        registry.Register(CreateSystemdUnitFileInfoProfile());
        registry.Register(CreateSystemdUnitPropertySetProfile());
        registry.Register(CreateSystemdJournalEntryProfile());
        registry.Register(CreateSystemdLoginSessionInfoProfile());
        registry.Register(CreateSystemdLoginUserInfoProfile());
        registry.Register(CreateSystemdLoginSeatInfoProfile());
        registry.Register(CreateSystemdPropertySetProfile());
        registry.Register(CreateSystemdHostInfoProfile());
        registry.Register(CreateSystemdNetworkLinkInfoProfile());
        registry.Register(CreateBlockDeviceProfile());
        registry.Register(CreateTreeEntryProfile());
        registry.Register(CreateRemovedEntryProfile());
        registry.Register(CreateCpuInfoProfile());
        registry.Register(CreateCpuTopologyProfile());
        registry.Register(CreateCpuCacheProfile());
        registry.Register(CreateFileDescriptorProfile());
        registry.Register(CreateSystemCounterProfile());
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
        registry.Register(CreateEndPointProfile());
        registry.Register(CreateMethodBaseProfile());
        registry.Register(CreatePropertyInfoProfile());
        registry.Register(CreateStackFrameProfile());
        registry.Register(CreateStackTraceProfile());
        registry.Register(CreateDictionaryEntryProfile());
        registry.Register(CreateAssemblyNameProfile());
        registry.Register(CreateTypeProfile());
        registry.Register(CreateHttpRequestDefinitionProfile());
        registry.Register(CreateHttpResponseInfoProfile());
        registry.Register(CreateHttpFileServerHandleProfile());
        registry.Register(CreateHttpRequestMessageProfile());
        registry.Register(CreateHttpResponseMessageProfile());
        registry.Register(CreateAssemblyProfile());
        registry.Register(CreateFieldInfoProfile());
        registry.Register(CreateEventInfoProfile());
        registry.Register(CreateParameterInfoProfile());
        registry.Register(CreateCookieProfile());
        registry.Register(CreateCookieCollectionProfile());
        registry.Register(CreateCookieContainerProfile());
        registry.Register(CreateNetworkCredentialProfile());
        registry.Register(CreatePhysicalAddressProfile());
        registry.Register(CreateIpHostEntryProfile());
        registry.Register(CreateWebHeaderCollectionProfile());
        registry.Register(CreateFileVersionInfoProfile());
        registry.Register(CreateNetworkInterfaceProfile());
        registry.Register(CreateIPInterfacePropertiesProfile());
        registry.Register(CreateUnicastIPAddressInformationProfile());
        registry.Register(CreateGatewayIPAddressInformationProfile());
        registry.Register(CreateTcpConnectionInformationProfile());
        registry.Register(CreatePingOptionsProfile());
        registry.Register(CreateHttpMethodProfile());
        registry.Register(CreateHttpStatusCodeProfile());
        registry.Register(CreateMediaTypeHeaderValueProfile());
        registry.Register(CreateAuthenticationHeaderValueProfile());
        registry.Register(CreateContentDispositionHeaderValueProfile());
        registry.Register(CreateEntityTagHeaderValueProfile());
        registry.Register(CreateCacheControlHeaderValueProfile());
        registry.Register(CreateAssemblyLoadContextProfile());
        registry.Register(CreateProcessStartInfoProfile());
        registry.Register(CreateProcessModuleProfile());
        registry.Register(CreateFileSystemWatcherProfile());
        registry.Register(CreateHttpRequestHeadersProfile());
        registry.Register(CreateHttpResponseHeadersProfile());
        registry.Register(CreateHttpContentHeadersProfile());
        registry.Register(CreateHttpHeadersProfile());
        registry.Register(CreateHttpContentProfile());
        registry.Register(CreateColorProfile());
        registry.Register(CreateEnumProfile());
        registry.Register(CreateUnixFileModeProfile(preferences));
        registry.Register(CreateFileAttributesProfile(preferences));
        registry.Register(CreateFileSystemPrincipalProfile());
        registry.Register(CreateTextStatisticsProfile());
        registry.Register(CreateDictionaryRecordProfile());
        registry.Register(CreateReadOnlyDictionaryRecordProfile());
        registry.Register(CreateObjectKeyedDictionaryProfile());
        registry.Register(CreateFileSystemEntryTypeProfile());
        registry.Register(CreateFileSystemEntryProfile(preferences));
        registry.Register(CreateFileSystemInfoProfile(preferences));
        registry.Register(CreateDriveInfoProfile());
        registry.Register(CreateUnameInfoProfile());
        registry.Register(CreateUserIdentityInfoProfile());
        registry.Register(CreateMountInfoProfile());
        registry.Register(CreateFileSystemUsageInfoProfile());
        registry.Register(CreatePathUsageInfoProfile());
        registry.Register(CreatePingReplyInfoProfile());
        registry.Register(CreateProcessProfile());
        registry.Register(CreateProcessInfoProfile());
        registry.Register(CreateProcessTreeInfoProfile());
        registry.Register(CreateShellJobStatusProfile());
        registry.Register(CreateShellJobInfoProfile());
        registry.Register(CreateShellJobCompletionProfile());
        registry.Register(CreateJobControlResultProfile());
        registry.Register(CreateGroupingInfoProfile());
        registry.Register(CreateEnvironmentVariableEntryProfile());
        registry.Register(CreateShellVariableEntryProfile());
        registry.Register(CreateHelpSubjectKindProfile());
        registry.Register(CreateHelpSummaryProfile());
        registry.Register(CreateHelpSearchResultProfile());
        registry.Register(CreateHelpTopicProfile());
        registry.Register(CreateHelpCategoryInfoProfile());
        registry.Register(CreateCommandResolutionKindProfile());
        registry.Register(CreateCommandResolutionProfile());
        registry.Register(CreateShellCommandDescriptorProfile());
        registry.Register(CreateCommandHistoryEntryProfile(preferences));
        registry.Register(CreateDirectoryStackEntryProfile());
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
        registry.Register(CreateCommandResultProfile());
        registry.Register(CreateStyledTextProfile());
        registry.Register(CreateEventRaiseResultProfile());
        registry.Register(CreateShellEventHandlerProfile());
        registry.Register(CreateEventHandlerRemovalResultProfile());
        registry.Register(CreateEventClearResultProfile());

        // Streams and I/O
        registry.Register(CreateStreamProfile());
        registry.Register(CreateStreamReaderProfile());
        registry.Register(CreateStreamWriterProfile());
        registry.Register(CreateZipArchiveProfile());
        registry.Register(CreateZipArchiveEntryProfile());

        // Platform and runtime
        registry.Register(CreateOperatingSystemProfile());
        registry.Register(CreateArchitectureProfile());
        registry.Register(CreateRuntimeInformationProfile());

        // Security and identity
        registry.Register(CreateX509Certificate2Profile());
        registry.Register(CreateX500DistinguishedNameProfile());
        registry.Register(CreateOidProfile());
        registry.Register(CreateClaimProfile());
        registry.Register(CreateClaimsIdentityProfile());
        registry.Register(CreateClaimsPrincipalProfile());

        // Numerics and geometry
        registry.Register(CreateBigIntegerProfile());
        registry.Register(CreateComplexProfile());
        registry.Register(CreateVector2Profile());
        registry.Register(CreateVector3Profile());
        registry.Register(CreateVector4Profile());
        registry.Register(CreateQuaternionProfile());
        registry.Register(CreateMatrix4x4Profile());

        // WebProxy
        registry.Register(CreateWebProxyProfile());
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

    private static DisplayProfile CreateIpAddressProfile()
    {
        return DisplayProfile
            .For<IPAddress>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildIpAddressColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((IPAddress)context.Value).ToString());
    }

    private static DisplayProfile CreateCommandTimingInfoProfile()
    {
        return DisplayProfile
            .For<CommandTimingInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCommandTimingInfoColumns());
    }

    private static DisplayProfile CreateManagedFileHandleProfile()
    {
        return DisplayProfile
            .For<ManagedFileHandle>()
            .AddTableCase(
                _ => BuildManagedFileHandleColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ManagedFileHandle)context.Value).ToString());
    }

    private static DisplayProfile CreateIpAddressInfoProfile()
    {
        return DisplayProfile
            .For<IpAddressInfo>()
            .AddTableCase(_ => BuildIpAddressInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((IpAddressInfo)context.Value).Cidr);
    }

    private static DisplayProfile CreateIpInterfaceProfile()
    {
        return DisplayProfile
            .For<IpInterfaceInfo>()
            .AddTableCase(_ => BuildIpInterfaceColumns())
            .AddSelectableTableColumns(_ => BuildIpInterfaceColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpInterfaceInfo)context.Value;
                    return string.IsNullOrWhiteSpace(value.State)
                        ? value.Name
                        : $"{value.Name} ({value.State})";
                });
    }

    private static DisplayProfile CreateIpRouteProfile()
    {
        return DisplayProfile
            .For<IpRouteInfo>()
            .AddTableCase(_ => BuildIpRouteColumns())
            .AddSelectableTableColumns(_ => BuildIpRouteColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpRouteInfo)context.Value;
                    var gateway = value.Gateway is null ? string.Empty : $" via {value.Gateway}";
                    var device = string.IsNullOrWhiteSpace(value.Device) ? string.Empty : $" dev {value.Device}";
                    return $"{value.Destination}{gateway}{device}".TrimEnd();
                });
    }

    private static DisplayProfile CreateIpNeighborProfile()
    {
        return DisplayProfile
            .For<IpNeighborInfo>()
            .AddTableCase(_ => BuildIpNeighborColumns())
            .AddSelectableTableColumns(_ => BuildIpNeighborColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpNeighborInfo)context.Value;
                    return $"{value.Address} ({value.StateText})";
                });
    }

    private static DisplayProfile CreateIpRuleProfile()
    {
        return DisplayProfile
            .For<IpRuleInfo>()
            .AddTableCase(_ => BuildIpRuleColumns())
            .AddSelectableTableColumns(_ => BuildIpRuleColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpRuleInfo)context.Value;
                    return $"{value.Priority}: from {value.SourceText} lookup {value.Table}";
                });
    }

    private static DisplayProfile CreateIpNetnsProfile()
    {
        return DisplayProfile
            .For<IpNetnsInfo>()
            .AddTableCase(_ => BuildIpNetnsColumns())
            .AddSelectableTableColumns(_ => BuildIpNetnsColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((IpNetnsInfo)context.Value).Name);
    }

    private static DisplayProfile CreateIpTunnelProfile()
    {
        return DisplayProfile
            .For<IpTunnelInfo>()
            .AddTableCase(_ => BuildIpTunnelColumns())
            .AddSelectableTableColumns(_ => BuildIpTunnelColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpTunnelInfo)context.Value;
                    var mode = string.IsNullOrWhiteSpace(value.Mode) ? string.Empty : $" ({value.Mode})";
                    return $"{value.Name}{mode}";
                });
    }

    private static DisplayProfile CreateIpTuntapProfile()
    {
        return DisplayProfile
            .For<IpTuntapInfo>()
            .AddTableCase(_ => BuildIpTuntapColumns())
            .AddSelectableTableColumns(_ => BuildIpTuntapColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpTuntapInfo)context.Value;
                    var mode = string.IsNullOrWhiteSpace(value.Mode) ? string.Empty : $" ({value.Mode})";
                    return $"{value.Name}{mode}";
                });
    }

    private static DisplayProfile CreateIpVrfProfile()
    {
        return DisplayProfile
            .For<IpVrfInfo>()
            .AddTableCase(_ => BuildIpVrfColumns())
            .AddSelectableTableColumns(_ => BuildIpVrfColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((IpVrfInfo)context.Value).Name);
    }

    private static DisplayProfile CreateIpMaddrProfile()
    {
        return DisplayProfile
            .For<IpMaddrInfo>()
            .AddTableCase(_ => BuildIpMaddrColumns())
            .AddSelectableTableColumns(_ => BuildIpMaddrColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpMaddrInfo)context.Value;
                    return $"{value.Name} ({value.AddressCount} addrs)";
                });
    }

    private static DisplayProfile CreateIpMaddrEntryProfile()
    {
        return DisplayProfile
            .For<IpMaddrEntry>()
            .AddTableCase(_ => BuildIpMaddrEntryColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpMaddrEntry)context.Value;
                    return value.Address ?? value.Link ?? string.Empty;
                });
    }

    private static DisplayProfile CreateIpMrouteProfile()
    {
        return DisplayProfile
            .For<IpMrouteInfo>()
            .AddTableCase(_ => BuildIpMrouteColumns())
            .AddSelectableTableColumns(_ => BuildIpMrouteColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpMrouteInfo)context.Value;
                    return $"{value.Group} from {value.Source ?? "any"}";
                });
    }

    private static DisplayProfile CreateIpTokenProfile()
    {
        return DisplayProfile
            .For<IpTokenInfo>()
            .AddTableCase(_ => BuildIpTokenColumns())
            .AddSelectableTableColumns(_ => BuildIpTokenColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpTokenInfo)context.Value;
                    var iface = string.IsNullOrWhiteSpace(value.InterfaceName) ? string.Empty : $" ({value.InterfaceName})";
                    return $"{value.Token}{iface}";
                });
    }

    private static DisplayProfile CreateIpNtableProfile()
    {
        return DisplayProfile
            .For<IpNtableInfo>()
            .AddTableCase(_ => BuildIpNtableColumns())
            .AddSelectableTableColumns(_ => BuildIpNtableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (IpNtableInfo)context.Value;
                    var dev = string.IsNullOrWhiteSpace(value.Dev) ? string.Empty : $" ({value.Dev})";
                    return $"{value.Name}{dev}";
                });
    }

    private static DisplayProfile CreateSystemdUnitInfoProfile()
    {
        return DisplayProfile
            .For<SystemdUnitInfo>()
            .AddTableCase(_ => BuildSystemdUnitInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdUnitInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdUnitInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdUnitFileInfoProfile()
    {
        return DisplayProfile
            .For<SystemdUnitFileInfo>()
            .AddTableCase(_ => BuildSystemdUnitFileInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdUnitFileInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdUnitFileInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdUnitPropertySetProfile()
    {
        return DisplayProfile
            .For<SystemdUnitPropertySet>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildSystemdUnitPropertySetColumns())
            .AddSelectableTableColumns(_ => BuildSystemdUnitPropertySetColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdUnitPropertySet)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdJournalEntryProfile()
    {
        return DisplayProfile
            .For<SystemdJournalEntry>()
            .AddTableCase(_ => BuildSystemdJournalEntryDefaultColumns())
            .AddSelectableTableColumns(_ => BuildSystemdJournalEntrySelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdJournalEntry)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdLoginSessionInfoProfile()
    {
        return DisplayProfile
            .For<SystemdLoginSessionInfo>()
            .AddTableCase(_ => BuildSystemdLoginSessionInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdLoginSessionInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdLoginSessionInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdLoginUserInfoProfile()
    {
        return DisplayProfile
            .For<SystemdLoginUserInfo>()
            .AddTableCase(_ => BuildSystemdLoginUserInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdLoginUserInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdLoginUserInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdLoginSeatInfoProfile()
    {
        return DisplayProfile
            .For<SystemdLoginSeatInfo>()
            .AddTableCase(_ => BuildSystemdLoginSeatInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdLoginSeatInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdLoginSeatInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdPropertySetProfile()
    {
        return DisplayProfile
            .For<SystemdPropertySet>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildSystemdPropertySetColumns())
            .AddSelectableTableColumns(_ => BuildSystemdPropertySetColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdPropertySet)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdHostInfoProfile()
    {
        return DisplayProfile
            .For<SystemdHostInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildSystemdHostInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdHostInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdHostInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemdNetworkLinkInfoProfile()
    {
        return DisplayProfile
            .For<SystemdNetworkLinkInfo>()
            .AddTableCase(_ => BuildSystemdNetworkLinkInfoColumns())
            .AddSelectableTableColumns(_ => BuildSystemdNetworkLinkInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemdNetworkLinkInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateBlockDeviceProfile()
    {
        return DisplayProfile
            .For<BlockDeviceInfo>()
            .AddTableCase(_ => BuildBlockDeviceDefaultColumns())
            .AddSelectableTableColumns(_ => BuildBlockDeviceSelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (BlockDeviceInfo)context.Value;
                    return string.IsNullOrWhiteSpace(value.Type)
                        ? value.Name
                        : $"{value.Name} ({value.Type})";
                });
    }

    private static DisplayProfile CreateTreeEntryProfile()
    {
        return DisplayProfile
            .For<TreeEntryInfo>()
            .AddTableCase(_ => BuildTreeEntryDefaultColumns())
            .AddSelectableTableColumns(_ => BuildTreeEntrySelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var entry = (TreeEntryInfo)context.Value;
                    return entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
                });
    }

    private static DisplayProfile CreateCpuInfoProfile()
    {
        return DisplayProfile
            .For<CpuInfo>()
            .AddTableCase(_ => BuildCpuInfoColumns())
            .AddSelectableTableColumns(_ => BuildCpuInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((CpuInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateCpuTopologyProfile()
    {
        return DisplayProfile
            .For<CpuTopologyInfo>()
            .AddTableCase(_ => BuildCpuTopologyDefaultColumns())
            .AddSelectableTableColumns(_ => BuildCpuTopologySelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((CpuTopologyInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateCpuCacheProfile()
    {
        return DisplayProfile
            .For<CpuCacheInfo>()
            .AddTableCase(_ => BuildCpuCacheDefaultColumns())
            .AddSelectableTableColumns(_ => BuildCpuCacheSelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((CpuCacheInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateFileDescriptorProfile()
    {
        return DisplayProfile
            .For<FileDescriptorInfo>()
            .AddTableCase(_ => BuildFileDescriptorDefaultColumns())
            .AddSelectableTableColumns(context => BuildFileDescriptorSelectableColumns(context.Rows))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileDescriptorInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateSystemCounterProfile()
    {
        return DisplayProfile
            .For<SystemCounterInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Counter", row => ((SystemCounterInfo)row).Counter, MinWidth: 8, MaxWidth: 32, Priority: 0, CanHide: false, SelectionKey: "COUNTER"),
                    new DisplayTableColumn("Value", row => ((SystemCounterInfo)row).Value, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 16, Priority: 10, SelectionKey: "VALUE"),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((SystemCounterInfo)context.Value).ToString());
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

    private static DisplayProfile CreateMountInfoProfile()
    {
        return DisplayProfile
            .For<MountInfo>()
            .AddTableCase(_ => BuildMountInfoDefaultColumns())
            .AddSelectableTableColumns(_ => BuildMountInfoSelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var value = (MountInfo)context.Value;
                    return string.IsNullOrWhiteSpace(value.Source)
                        ? value.Target
                        : $"{value.Target} <- {value.Source}";
                });
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

    private static DisplayProfile CreateEndPointProfile()
    {
        return DisplayProfile
            .For<EndPoint>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildEndPointColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatEndPointValue((EndPoint)context.Value));
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

    private static DisplayProfile CreateHttpRequestMessageProfile()
    {
        return DisplayProfile
            .For<HttpRequestMessage>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpRequestMessageColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpRequestMessageSummary((HttpRequestMessage)context.Value));
    }

    private static DisplayProfile CreateHttpRequestDefinitionProfile()
    {
        return DisplayProfile
            .For<HttpRequestDefinition>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpRequestDefinitionColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpRequestDefinitionSummary((HttpRequestDefinition)context.Value));
    }

    private static DisplayProfile CreateHttpResponseMessageProfile()
    {
        return DisplayProfile
            .For<HttpResponseMessage>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpResponseMessageColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpResponseMessageSummary((HttpResponseMessage)context.Value));
    }

    private static DisplayProfile CreateHttpResponseInfoProfile()
    {
        return DisplayProfile
            .For<HttpResponseInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpResponseInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpResponseInfoSummary((HttpResponseInfo)context.Value));
    }

    private static DisplayProfile CreateHttpFileServerHandleProfile()
    {
        return DisplayProfile
            .For<HttpFileServerHandle>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpFileServerHandleColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpFileServerHandleSummary((HttpFileServerHandle)context.Value));
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

    private static DisplayProfile CreateCookieProfile()
    {
        return DisplayProfile
            .For<Cookie>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCookieColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatCookieSummary((Cookie)context.Value));
    }

    private static DisplayProfile CreateCookieCollectionProfile()
    {
        return DisplayProfile
            .For<CookieCollection>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCookieCollectionColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatCookieCollectionSummary((CookieCollection)context.Value));
    }

    private static DisplayProfile CreateCookieContainerProfile()
    {
        return DisplayProfile
            .For<CookieContainer>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCookieContainerColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatCookieContainerSummary((CookieContainer)context.Value));
    }

    private static DisplayProfile CreateNetworkCredentialProfile()
    {
        return DisplayProfile
            .For<NetworkCredential>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildNetworkCredentialColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatNetworkCredentialSummary((NetworkCredential)context.Value));
    }

    private static DisplayProfile CreatePhysicalAddressProfile()
    {
        return DisplayProfile
            .For<PhysicalAddress>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildPhysicalAddressColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatPhysicalAddressValue((PhysicalAddress)context.Value));
    }

    private static DisplayProfile CreateIpHostEntryProfile()
    {
        return DisplayProfile
            .For<IPHostEntry>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildIpHostEntryColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatIpHostEntrySummary((IPHostEntry)context.Value));
    }

    private static DisplayProfile CreateWebHeaderCollectionProfile()
    {
        return DisplayProfile
            .For<WebHeaderCollection>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildWebHeaderCollectionColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatWebHeaderCollectionSummary((WebHeaderCollection)context.Value));
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

    private static DisplayProfile CreateNetworkInterfaceProfile()
    {
        return DisplayProfile
            .For<NetworkInterface>()
            .AddTableCase(
                _ => BuildNetworkInterfaceColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatNetworkInterfaceSummary((NetworkInterface)context.Value));
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

    private static DisplayProfile CreateProcessStartInfoProfile()
    {
        return DisplayProfile
            .For<ProcessStartInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildProcessStartInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatProcessStartInfoSummary((ProcessStartInfo)context.Value));
    }

    private static DisplayProfile CreateProcessModuleProfile()
    {
        return DisplayProfile
            .For<ProcessModule>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildProcessModuleColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatProcessModuleSummary((ProcessModule)context.Value));
    }

    private static DisplayProfile CreateFileSystemWatcherProfile()
    {
        return DisplayProfile
            .For<FileSystemWatcher>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileSystemWatcherColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatFileSystemWatcherSummary((FileSystemWatcher)context.Value));
    }

    private static DisplayProfile CreateHttpRequestHeadersProfile()
    {
        return DisplayProfile
            .For<HttpRequestHeaders>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpRequestHeadersColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpHeaders((HttpRequestHeaders)context.Value));
    }

    private static DisplayProfile CreateHttpResponseHeadersProfile()
    {
        return DisplayProfile
            .For<HttpResponseHeaders>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpResponseHeadersColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpHeaders((HttpResponseHeaders)context.Value));
    }

    private static DisplayProfile CreateHttpContentHeadersProfile()
    {
        return DisplayProfile
            .For<HttpContentHeaders>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpContentHeadersColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpHeaders((HttpContentHeaders)context.Value));
    }

    private static DisplayProfile CreateHttpHeadersProfile()
    {
        return DisplayProfile
            .For<HttpHeaders>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpHeadersColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpHeaders((HttpHeaders)context.Value));
    }

    private static DisplayProfile CreateHttpContentProfile()
    {
        return DisplayProfile
            .For<HttpContent>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpContentColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatHttpContentSummary((HttpContent)context.Value));
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

    private static DisplayProfile CreateUnixFileModeProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<UnixFileMode>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildUnixFileModeColumns(preferences))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatPermissions((UnixFileMode)context.Value, preferences.UnixFileMode.Mode));
    }

    private static DisplayProfile CreateFileAttributesProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<FileAttributes>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileAttributesColumns(preferences))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatFileAttributes((FileAttributes)context.Value, preferences.FileAttributes.Mode));
    }

    private static DisplayProfile CreateFileSystemPrincipalProfile()
    {
        return DisplayProfile
            .For<FileSystemPrincipalInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileSystemPrincipalColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileSystemPrincipalInfo)context.Value).DisplayName);
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

    private static string FormatDictKey(object key)
    {
        return key is string s ? $"\"{s}\"" : key?.ToString() ?? "null";
    }

    private static DisplayProfile CreateFileSystemEntryTypeProfile()
    {
        return DisplayProfile
            .For<FileSystemEntryType>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileSystemEntryType)context.Value) switch
                {
                    FileSystemEntryType.Dir => "dir",
                    FileSystemEntryType.File => "file",
                    _ => context.Value.ToString() ?? context.Value.GetType().Name,
                });
    }

    private static DisplayProfile CreateFileSystemEntryProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<FileSystemEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ((FileSystemEntry)context.Value).PreferLongDisplay,
                context =>
                {
                    var entry = (FileSystemEntry)context.Value;
                    var size = entry.Size is StorageSize storageSize
                        ? FormatStorageSize(storageSize, preferences.StorageSize.Mode)
                        : "-";
                    var timestamp = FormatDateTimeOffset(
                        entry.DisplayTime,
                        preferences.DateTimeOffset.TableMode,
                        preferences.DateTimeOffset.TableFormat,
                        preferences.NowProvider);
                    var owner = entry.Owner?.DisplayName ?? "-";
                    var group = entry.Group?.DisplayName ?? "-";
                    return $"{entry.GetModeDisplay(includeTypeIndicator: true)} {owner}:{group} {size,10} {timestamp} {entry.DisplayName}";
                })
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => context.Style != ObjectRenderStyle.Detail,
                context => ((FileSystemEntry)context.Value).DisplayName)
            .AddTableCase(context => BuildFileSystemEntryColumns(context.Rows));
    }

    private static DisplayProfile CreateFileSystemInfoProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<FileSystemInfo>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => GetDisplayEntry((FileSystemInfo)context.Value).DisplayName)
            .AddTableCase(context => BuildFileSystemInfoColumns(context.Rows));
    }

    private static DisplayProfile CreateDriveInfoProfile()
    {
        return DisplayProfile
            .For<DriveInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var drive = (DriveInfo)context.Value;
                    return drive.IsReady
                        ? $"{drive.Name} ({drive.DriveType})"
                        : $"{drive.Name} (not ready)";
                })
            .AddTableCase(
                context => BuildDriveInfoColumns(context.Rows));
    }

    private static DisplayProfile CreateProcessProfile()
    {
        return DisplayProfile
            .For<Process>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var process = ProcessInfo.From((Process)context.Value);
                    return $"{process.Id,6} {process.Name}";
                })
            .AddTableCase(
                context => BuildProcessColumns(context.Rows));
    }

    private static DisplayProfile CreateShellCommandDescriptorProfile()
    {
        return DisplayProfile
            .For<ShellCommandDescriptor>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var descriptor = (ShellCommandDescriptor)context.Value;
                    return $"{descriptor.Name.PadRight(12)} {descriptor.Description} ({descriptor.Usage})";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ShellCommandDescriptor)row).Name, MinWidth: 8, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Description", row => ((ShellCommandDescriptor)row).Description, MinWidth: 18, MaxWidth: 48, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Usage", row => ((ShellCommandDescriptor)row).Usage, MinWidth: 18, MaxWidth: 48, Priority: 20),
                ]);
    }

    private static DisplayProfile CreateUnameInfoProfile()
    {
        return DisplayProfile
            .For<UnameInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("SystemName", row => ((UnameInfo)row).SystemName, MinWidth: 6, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("NodeName", row => ((UnameInfo)row).NodeName, MinWidth: 6, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("Release", row => ((UnameInfo)row).Release, MinWidth: 6, MaxWidth: 24, Priority: 20),
                    new DisplayTableColumn("Version", row => ((UnameInfo)row).Version, MinWidth: 8, MaxWidth: 48, Priority: 30),
                    new DisplayTableColumn("Machine", row => ((UnameInfo)row).Machine, MinWidth: 6, MaxWidth: 16, Priority: 40),
                    new DisplayTableColumn("OperatingSystem", row => ((UnameInfo)row).OperatingSystem, MinWidth: 8, MaxWidth: 24, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateUserIdentityInfoProfile()
    {
        return DisplayProfile
            .For<UserIdentityInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("User", row => ((UserIdentityInfo)row).User, MinWidth: 6, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Uid", row => ((UserIdentityInfo)row).Uid, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Group", row => ((UserIdentityInfo)row).Group, MinWidth: 6, MaxWidth: 20, Priority: 20),
                    new DisplayTableColumn("Gid", row => ((UserIdentityInfo)row).Gid, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 30),
                    new DisplayTableColumn("Euid", row => ((UserIdentityInfo)row).Euid, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 40),
                    new DisplayTableColumn("Egid", row => ((UserIdentityInfo)row).Egid, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 50),
                    new DisplayTableColumn("Groups", row => string.Join(", ", ((UserIdentityInfo)row).Groups.Select(group => group.DisplayName)), MinWidth: 8, MaxWidth: 48, Priority: 60),
                ]);
    }

    private static DisplayProfile CreateFileSystemUsageInfoProfile()
    {
        return DisplayProfile
            .For<FileSystemUsageInfo>()
            .AddTableCase(
                context =>
                {
                    var items = context.Rows.Cast<FileSystemUsageInfo>().ToArray();
                    var columns = new List<DisplayTableColumn>();

                    if (items.Any(item => !string.IsNullOrWhiteSpace(item.RequestedPath)))
                    {
                        columns.Add(new DisplayTableColumn("Path", row => ((FileSystemUsageInfo)row).RequestedPath, MinWidth: 8, MaxWidth: 40, Priority: 0));
                    }

                    columns.AddRange(
                    [
                        new DisplayTableColumn("FileSystem", row => ((FileSystemUsageInfo)row).FileSystem, MinWidth: 10, MaxWidth: 28, Priority: 10, CanHide: false),
                        new DisplayTableColumn("Type", row => ((FileSystemUsageInfo)row).Type, MinWidth: 4, MaxWidth: 14, Priority: 20),
                        new DisplayTableColumn("Size", row => ((FileSystemUsageInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                        new DisplayTableColumn("Used", row => ((FileSystemUsageInfo)row).Used, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 40),
                        new DisplayTableColumn("Available", row => ((FileSystemUsageInfo)row).Available, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 50),
                        new DisplayTableColumn("Use%", row => FormatUsePercent(((FileSystemUsageInfo)row).UsePercent), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 5, Priority: 60, SelectionKey: "UsePercent"),
                        new DisplayTableColumn("MountedOn", row => ((FileSystemUsageInfo)row).MountedOn, MinWidth: 6, MaxWidth: 28, Priority: 70, CanHide: false),
                    ]);

                    return columns;
                });
    }

    private static DisplayProfile CreatePathUsageInfoProfile()
    {
        return DisplayProfile
            .For<PathUsageInfo>()
            .AddTableCase(
                context =>
                {
                    var items = context.Rows.Cast<PathUsageInfo>().ToArray();
                    var columns = new List<DisplayTableColumn>
                    {
                        new("Name", row => ((PathUsageInfo)row).Name, MinWidth: 8, MaxWidth: 40, Priority: 0, CanHide: false),
                        new("Type", row => ((PathUsageInfo)row).Type, MinWidth: 4, MaxWidth: 8, Priority: 10),
                        new("Size", row => ((PathUsageInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                        new("Depth", row => ((PathUsageInfo)row).Depth, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 30),
                    };

                    if (items.Any(item => item.Modified is not null))
                    {
                        columns.Add(new DisplayTableColumn("Modified", row => ((PathUsageInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 40));
                    }

                    return columns;
                })
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var value = (PathUsageInfo)context.Value;
                    return $"{value.Size} {value.FullName}";
                });
    }

    private static DisplayProfile CreatePingReplyInfoProfile()
    {
        return DisplayProfile
            .For<PingReplyInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Sequence", row => ((PingReplyInfo)row).Sequence, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Address", row => ((PingReplyInfo)row).Address, MinWidth: 7, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("Status", row => ((PingReplyInfo)row).Status, MinWidth: 7, MaxWidth: 16, Priority: 20),
                    new DisplayTableColumn("Time", row => FormatPingDuration(((PingReplyInfo)row).RoundtripTime), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("Ttl", row => ((PingReplyInfo)row).Ttl, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 5, Priority: 40),
                    new DisplayTableColumn("Bytes", row => ((PingReplyInfo)row).Bytes, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateProcessInfoProfile()
    {
        return DisplayProfile
            .For<ProcessInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var process = (ProcessInfo)context.Value;
                    return $"{process.Id,6} {process.Name}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ProcessInfo)row).Name, MinWidth: 10, MaxWidth: 24, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Id", row => ((ProcessInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
                    new DisplayTableColumn("Memory", row => ((ProcessInfo)row).Memory, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                    new DisplayTableColumn("Cpu", row => ((ProcessInfo)row).Cpu, MinWidth: 6, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("Started", row => ((ProcessInfo)row).Started, MinWidth: 11, MaxWidth: 18, Priority: 40),
                    new DisplayTableColumn("Path", row => ((ProcessInfo)row).Path, MinWidth: 16, MaxWidth: 48, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateProcessTreeInfoProfile()
    {
        return DisplayProfile
            .For<ProcessTreeInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ProcessTreeInfo)row).Name, MinWidth: 10, MaxWidth: 40, Priority: 0, CanHide: false, IsTree: true),
                    new DisplayTableColumn("Id", row => ((ProcessTreeInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
                    new DisplayTableColumn("Memory", row => ((ProcessTreeInfo)row).Memory, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                    new DisplayTableColumn("Cpu", row => ((ProcessTreeInfo)row).Cpu, MinWidth: 6, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("User", row => ((ProcessTreeInfo)row).UserName, MinWidth: 4, MaxWidth: 16, Priority: 40),
                ]);
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemEntryColumns(IReadOnlyList<object> rows)
    {
        var entries = rows.Cast<FileSystemEntry>().ToArray();
        var showLongMetadata = entries.Any(entry => entry.PreferLongDisplay);
        var showTarget = entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Target));
        var showInodeInShortDisplay = entries.Any(entry => entry.IncludeInodeInShortDisplay);
        var timeFieldEntry = entries.FirstOrDefault() ?? throw new InvalidOperationException("Expected at least one file-system entry row.");
        var timeHeader = timeFieldEntry.DisplayTimeColumnName;
        Func<object, object?> timeAccessor = row => ((FileSystemEntry)row).DisplayTime;

        if (!showLongMetadata)
        {
            var shortColumns = new List<DisplayTableColumn>();

            if (showInodeInShortDisplay)
            {
                shortColumns.Add(new DisplayTableColumn("Inode", row => ((FileSystemEntry)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 0));
            }

            shortColumns.AddRange(
            [
                new DisplayTableColumn("Name", row => ((FileSystemEntry)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 10, CanHide: false, IsTree: true),
                new DisplayTableColumn("Type", row => ((FileSystemEntry)row).Type, MaxWidth: 8, Priority: 20),
                new DisplayTableColumn("Size", row => ((FileSystemEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                new DisplayTableColumn(timeHeader, timeAccessor, MinWidth: 11, MaxWidth: 18, Priority: 40),
            ]);

            return shortColumns;
        }

        var columns = new List<DisplayTableColumn>
        {
            new("Name", row => ((FileSystemEntry)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false, IsTree: true),
            new("Type", row => ((FileSystemEntry)row).Type, MaxWidth: 8, Priority: 10),
        };

        if (showTarget)
        {
            columns.Add(new DisplayTableColumn("Target", row => ((FileSystemEntry)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 95));
        }

        columns.AddRange(
        [
            new DisplayTableColumn("Size", row => ((FileSystemEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn(timeHeader, timeAccessor, MinWidth: 11, MaxWidth: 18, Priority: 30),
            new DisplayTableColumn("Readonly", row => ((FileSystemEntry)row).Readonly, MinWidth: 5, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("Mode", row => ((FileSystemEntry)row).Mode, MinWidth: 9, MaxWidth: 18, Priority: 50),
            new DisplayTableColumn("NumLinks", row => ((FileSystemEntry)row).NumLinks, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 60),
            new DisplayTableColumn("Inode", row => ((FileSystemEntry)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Owner", row => ((FileSystemEntry)row).Owner, MinWidth: 4, MaxWidth: 16, Priority: 80),
            new DisplayTableColumn("Group", row => ((FileSystemEntry)row).Group, MinWidth: 4, MaxWidth: 16, Priority: 90),
            new DisplayTableColumn("Created", row => ((FileSystemEntry)row).Created, MinWidth: 11, MaxWidth: 18, Priority: 100),
            new DisplayTableColumn("Accessed", row => ((FileSystemEntry)row).Accessed, MinWidth: 11, MaxWidth: 18, Priority: 110),
        ]);

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemInfoColumns(IReadOnlyList<object> rows)
    {
        if (rows.Count == 1)
        {
            return
            [
                new DisplayTableColumn("Name", row => GetDisplayEntry((FileSystemInfo)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false),
                new DisplayTableColumn("FullName", row => ((FileSystemInfo)row).FullName, MinWidth: 16, MaxWidth: 72, Priority: 5, CanHide: false),
                new DisplayTableColumn("Type", row => GetDisplayEntry((FileSystemInfo)row).Type, MaxWidth: 8, Priority: 10),
                new DisplayTableColumn("Size", row => GetDisplayEntry((FileSystemInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                new DisplayTableColumn("Modified", row => GetDisplayEntry((FileSystemInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30),
                new DisplayTableColumn("Attributes", row => ((FileSystemInfo)row).Attributes, MinWidth: 6, MaxWidth: 28, Priority: 40),
                new DisplayTableColumn("Target", row => GetDisplayEntry((FileSystemInfo)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 50),
            ];
        }

        var entries = rows.Cast<FileSystemInfo>().Select(GetDisplayEntry).ToArray();
        var showTarget = entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Target));

        var columns = new List<DisplayTableColumn>
        {
            new("Name", row => GetDisplayEntry((FileSystemInfo)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false),
            new("Type", row => GetDisplayEntry((FileSystemInfo)row).Type, MaxWidth: 8, Priority: 10),
            new("Size", row => GetDisplayEntry((FileSystemInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new("Modified", row => GetDisplayEntry((FileSystemInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30),
        };

        if (showTarget)
        {
            columns.Add(new DisplayTableColumn("Target", row => GetDisplayEntry((FileSystemInfo)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 40));
        }

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildDriveInfoColumns(IReadOnlyList<object> rows)
    {
        if (rows.Count == 1)
        {
            return
            [
                new DisplayTableColumn("Name", row => ((DriveInfo)row).Name, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                new DisplayTableColumn("DriveType", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveType), MinWidth: 4, MaxWidth: 16, Priority: 10),
                new DisplayTableColumn("DriveFormat", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveFormat), MinWidth: 4, MaxWidth: 16, Priority: 20),
                new DisplayTableColumn("VolumeLabel", row => SafeGetDriveValue((DriveInfo)row, drive => drive.VolumeLabel), MinWidth: 4, MaxWidth: 24, Priority: 30),
                new DisplayTableColumn("RootDirectory", row => ((DriveInfo)row).RootDirectory, MinWidth: 4, MaxWidth: 24, Priority: 40),
                new DisplayTableColumn("TotalSize", row => SafeGetDriveSize((DriveInfo)row, drive => drive.TotalSize), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 50),
                new DisplayTableColumn("AvailableFreeSpace", row => SafeGetDriveSize((DriveInfo)row, drive => drive.AvailableFreeSpace), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 60),
                new DisplayTableColumn("TotalFreeSpace", row => SafeGetDriveSize((DriveInfo)row, drive => drive.TotalFreeSpace), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
                new DisplayTableColumn("IsReady", row => ((DriveInfo)row).IsReady, MinWidth: 5, MaxWidth: 5, Priority: 80),
            ];
        }

        return
        [
            new DisplayTableColumn("Name", row => ((DriveInfo)row).Name, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
            new DisplayTableColumn("DriveType", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveType), MinWidth: 4, MaxWidth: 16, Priority: 10),
            new DisplayTableColumn("DriveFormat", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveFormat), MinWidth: 4, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("TotalSize", row => SafeGetDriveSize((DriveInfo)row, drive => drive.TotalSize), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
            new DisplayTableColumn("Available", row => SafeGetDriveSize((DriveInfo)row, drive => drive.AvailableFreeSpace), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 40),
            new DisplayTableColumn("IsReady", row => ((DriveInfo)row).IsReady, MinWidth: 5, MaxWidth: 5, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildProcessColumns(IReadOnlyList<object> rows)
    {
        return
        [
            new DisplayTableColumn("Name", row => ProcessInfo.From((Process)row).Name, MinWidth: 10, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Id", row => ProcessInfo.From((Process)row).Id, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("Memory", row => ProcessInfo.From((Process)row).Memory, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("Cpu", row => ProcessInfo.From((Process)row).Cpu, MinWidth: 6, MaxWidth: 12, Priority: 30),
            new DisplayTableColumn("Started", row => ProcessInfo.From((Process)row).Started, MinWidth: 11, MaxWidth: 18, Priority: 40),
            new DisplayTableColumn("Path", row => ProcessInfo.From((Process)row).Path, MinWidth: 16, MaxWidth: 48, Priority: 50),
        ];
    }

    private static DisplayProfile CreateShellJobStatusProfile()
    {
        return DisplayProfile
            .For<ShellJobStatus>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ShellJobStatus)context.Value).ToString().ToLowerInvariant());
    }

    private static DisplayProfile CreateShellJobInfoProfile()
    {
        return DisplayProfile
            .For<ShellJobInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var job = (ShellJobInfo)context.Value;
                    var pid = job.ProcessId is int processId ? $" pid={processId}" : string.Empty;
                    return $"[{job.Id}] {job.Status.ToString().ToLowerInvariant()}{pid} {job.Command}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Id", row => ((ShellJobInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Status", row => ((ShellJobInfo)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Pid", row => ((ShellJobInfo)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                    new DisplayTableColumn("ExitCode", row => ((ShellJobInfo)row).ExitCode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 30),
                    new DisplayTableColumn("Started", row => ((ShellJobInfo)row).StartedAt, MinWidth: 11, MaxWidth: 18, Priority: 40),
                    new DisplayTableColumn("Duration", row => ((ShellJobInfo)row).Duration, MinWidth: 6, MaxWidth: 14, Priority: 50),
                    new DisplayTableColumn("Command", row => ((ShellJobInfo)row).Command, MinWidth: 12, MaxWidth: 64, Priority: 60, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateShellJobCompletionProfile()
    {
        return DisplayProfile
            .For<ShellJobCompletion>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var job = (ShellJobCompletion)context.Value;
                    var exitCode = job.ExitCode is int code ? $" exit={code}" : string.Empty;
                    return $"[{job.Id}] {job.Status.ToString().ToLowerInvariant()}{exitCode} {job.Command}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Id", row => ((ShellJobCompletion)row).Id, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Status", row => ((ShellJobCompletion)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Pid", row => ((ShellJobCompletion)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                    new DisplayTableColumn("ExitCode", row => ((ShellJobCompletion)row).ExitCode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 30),
                    new DisplayTableColumn("Duration", row => ((ShellJobCompletion)row).Duration, MinWidth: 6, MaxWidth: 14, Priority: 40),
                    new DisplayTableColumn("OutputCount", row => ((ShellJobCompletion)row).OutputCount, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 50),
                    new DisplayTableColumn("ErrorCount", row => ((ShellJobCompletion)row).ErrorCount, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 60),
                    new DisplayTableColumn("Command", row => ((ShellJobCompletion)row).Command, MinWidth: 12, MaxWidth: 64, Priority: 70, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateJobControlResultProfile()
    {
        return DisplayProfile
            .For<JobControlResult>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var result = (JobControlResult)context.Value;
                    var scope = result.JobId is int jobId
                        ? $"[{jobId}]"
                        : result.ProcessId is int processId
                            ? $"pid={processId}"
                            : string.Empty;
                    return string.IsNullOrWhiteSpace(scope)
                        ? $"{result.Action}: {result.Message}"
                        : $"{result.Action} {scope}: {result.Message}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Action", row => ((JobControlResult)row).Action, MinWidth: 4, MaxWidth: 10, Priority: 0, CanHide: false),
                    new DisplayTableColumn("JobId", row => ((JobControlResult)row).JobId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 10),
                    new DisplayTableColumn("Pid", row => ((JobControlResult)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                    new DisplayTableColumn("Status", row => ((JobControlResult)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 30),
                    new DisplayTableColumn("Success", row => ((JobControlResult)row).IsSuccess, MinWidth: 5, MaxWidth: 5, Priority: 40),
                    new DisplayTableColumn("Message", row => ((JobControlResult)row).Message, MinWidth: 12, MaxWidth: 64, Priority: 50, CanHide: false),
                ]);
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

    private static DisplayProfile CreateEnvironmentVariableEntryProfile()
    {
        return DisplayProfile
            .For<EnvironmentVariableEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var variable = (EnvironmentVariableEntry)context.Value;
                    return variable.IsSet
                        ? $"{variable.Name}={variable.Value}"
                        : $"{variable.Name}=<unset>";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((EnvironmentVariableEntry)row).Name, MinWidth: 10, MaxWidth: 24, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Value", row => ((EnvironmentVariableEntry)row).Value, MinWidth: 12, MaxWidth: 64, Priority: 10),
                    new DisplayTableColumn("Set", row => ((EnvironmentVariableEntry)row).IsSet, MinWidth: 3, MaxWidth: 5, Priority: 20),
                ]);
    }

    private static DisplayProfile CreateShellVariableEntryProfile()
    {
        return DisplayProfile
            .For<Sys.ShellVariableEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var entry = (Sys.ShellVariableEntry)context.Value;
                    return $"${entry.Name}: {entry.Type} = {entry.Value}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((Sys.ShellVariableEntry)row).Name, MinWidth: 8, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Type", row => ((Sys.ShellVariableEntry)row).Type, MinWidth: 6, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("Value", row => FormatVariableValue(((Sys.ShellVariableEntry)row).Value), MinWidth: 10, MaxWidth: 48, Priority: 20),
                ]);
    }

    private static string FormatVariableValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var text = value.ToString() ?? string.Empty;
        return text.Length > 80 ? text[..77] + "..." : text;
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

    private static DisplayProfile CreateCommandResolutionKindProfile()
    {
        return DisplayProfile
            .For<CommandResolutionKind>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((CommandResolutionKind)context.Value) switch
                {
                    CommandResolutionKind.BuiltIn => "builtin",
                    CommandResolutionKind.Alias => "alias",
                    CommandResolutionKind.Function => "function",
                    CommandResolutionKind.External => "external",
                    _ => context.Value.ToString() ?? context.Value.GetType().Name,
                });
    }

    private static DisplayProfile CreateCommandResolutionProfile()
    {
        return DisplayProfile
            .For<CommandResolution>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var resolution = (CommandResolution)context.Value;
                    return resolution.Path is not null
                        ? $"{resolution.Kind}: {resolution.Path}"
                        : $"{resolution.Kind}: {resolution.Name}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((CommandResolution)row).Name, MinWidth: 8, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((CommandResolution)row).Kind, MinWidth: 7, MaxWidth: 8, Priority: 10),
                    new DisplayTableColumn("Path", row => ((CommandResolution)row).Path, MinWidth: 12, MaxWidth: 64, Priority: 20),
                    new DisplayTableColumn("Usage", row => ((CommandResolution)row).Usage, MinWidth: 12, MaxWidth: 32, Priority: 30),
                ]);
    }

    private static DisplayProfile CreateCommandHistoryEntryProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<CommandHistoryEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var entry = (CommandHistoryEntry)context.Value;
                    var timestamp = FormatDateTimeOffset(
                        entry.Timestamp,
                        preferences.DateTimeOffset.ScalarMode,
                        preferences.DateTimeOffset.ScalarFormat,
                        preferences.NowProvider);
                    return $"{entry.Id,4}  {timestamp}  {entry.Text}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Id", row => ((CommandHistoryEntry)row).Id, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 8, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Text", row => ((CommandHistoryEntry)row).Text, MinWidth: 12, MaxWidth: 64, Priority: 10, CanHide: false),
                    new DisplayTableColumn("When", row => ((CommandHistoryEntry)row).When, MinWidth: 11, MaxWidth: 18, Priority: 20),
                ]);
    }

    private static DisplayProfile CreateDirectoryStackEntryProfile()
    {
        return DisplayProfile
            .For<DirectoryStackEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var entry = (DirectoryStackEntry)context.Value;
                    var marker = entry.IsCurrent ? "*" : " ";
                    return $"{marker} {entry.Index,3}  {entry.Path}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Index", row => ((DirectoryStackEntry)row).Index, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Path", row => ((DirectoryStackEntry)row).Path, MinWidth: 16, MaxWidth: 80, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Name", row => ((DirectoryStackEntry)row).Name, MinWidth: 8, MaxWidth: 32, Priority: 20),
                    new DisplayTableColumn("Current", row => ((DirectoryStackEntry)row).IsCurrent, MinWidth: 5, MaxWidth: 7, Priority: 30),
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

    private static string FormatJsonElementPreview(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => $"{{...}} ({element.EnumerateObject().Count()} properties)",
            JsonValueKind.Array => $"[...] ({element.GetArrayLength()} items)",
            _ => FormatJsonElementValue(element),
        };
    }

    private static string FormatJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "null",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "<undefined>",
            JsonValueKind.Object => $"{{...}} ({element.EnumerateObject().Count()} properties)",
            JsonValueKind.Array => $"[...] ({element.GetArrayLength()} items)",
            _ => element.GetRawText(),
        };
    }

    private static string FormatJsonNodePreview(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => $"{{...}} ({obj.Count} properties)",
            JsonArray arr => $"[...] ({arr.Count} items)",
            JsonValue val => val.ToJsonString(),
            _ => node.ToJsonString(),
        };
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

    private static DisplayProfile CreateIPInterfacePropertiesProfile()
    {
        return DisplayProfile
            .For<IPInterfaceProperties>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildIPInterfacePropertiesColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var props = (IPInterfaceProperties)context.Value;
                    var dns = props.DnsAddresses;
                    return dns.Count > 0
                        ? $"DNS: {string.Join(", ", dns.Take(3))}{(dns.Count > 3 ? ", ..." : "")}"
                        : "<no DNS>";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIPInterfacePropertiesColumns()
    {
        return
        [
            new DisplayTableColumn("DnsSuffix", row => NullIfEmpty(((IPInterfaceProperties)row).DnsSuffix), MinWidth: 4, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("DnsAddresses", row => FormatIpAddressCollection(((IPInterfaceProperties)row).DnsAddresses), MinWidth: 4, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("UnicastAddresses", row => ((IPInterfaceProperties)row).UnicastAddresses.Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("GatewayAddresses", row => ((IPInterfaceProperties)row).GatewayAddresses.Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 30),
        ];
    }

    private static DisplayProfile CreateUnicastIPAddressInformationProfile()
    {
        return DisplayProfile
            .For<UnicastIPAddressInformation>()
            .AddTableCase(_ => BuildUnicastIPAddressInformationColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((UnicastIPAddressInformation)context.Value).Address.ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildUnicastIPAddressInformationColumns()
    {
        return
        [
            new DisplayTableColumn("Address", row => ((UnicastIPAddressInformation)row).Address, MinWidth: 7, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("PrefixLength", row => ((UnicastIPAddressInformation)row).PrefixLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 10),
            new DisplayTableColumn("Family", row => ((UnicastIPAddressInformation)row).Address.AddressFamily, MinWidth: 4, MaxWidth: 22, Priority: 20),
        ];
    }

    private static DisplayProfile CreateGatewayIPAddressInformationProfile()
    {
        return DisplayProfile
            .For<GatewayIPAddressInformation>()
            .AddTableCase(_ => BuildGatewayIPAddressInformationColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((GatewayIPAddressInformation)context.Value).Address.ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildGatewayIPAddressInformationColumns()
    {
        return
        [
            new DisplayTableColumn("Address", row => ((GatewayIPAddressInformation)row).Address, MinWidth: 7, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Family", row => ((GatewayIPAddressInformation)row).Address.AddressFamily, MinWidth: 4, MaxWidth: 22, Priority: 10),
        ];
    }

    private static DisplayProfile CreateTcpConnectionInformationProfile()
    {
        return DisplayProfile
            .For<System.Net.NetworkInformation.TcpConnectionInformation>()
            .AddTableCase(_ => BuildTcpConnectionInformationColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var tcp = (System.Net.NetworkInformation.TcpConnectionInformation)context.Value;
                    return $"{tcp.LocalEndPoint} → {tcp.RemoteEndPoint} ({tcp.State})";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTcpConnectionInformationColumns()
    {
        return
        [
            new DisplayTableColumn("Local", row => ((System.Net.NetworkInformation.TcpConnectionInformation)row).LocalEndPoint, MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Remote", row => ((System.Net.NetworkInformation.TcpConnectionInformation)row).RemoteEndPoint, MinWidth: 8, MaxWidth: 48, Priority: 10, CanHide: false),
            new DisplayTableColumn("State", row => ((System.Net.NetworkInformation.TcpConnectionInformation)row).State, MinWidth: 5, MaxWidth: 18, Priority: 20),
        ];
    }

    private static DisplayProfile CreatePingOptionsProfile()
    {
        return DisplayProfile
            .For<PingOptions>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildPingOptionsColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var opts = (PingOptions)context.Value;
                    return $"TTL={opts.Ttl}, DontFragment={opts.DontFragment}";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildPingOptionsColumns()
    {
        return
        [
            new DisplayTableColumn("Ttl", row => ((PingOptions)row).Ttl, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("DontFragment", row => ((PingOptions)row).DontFragment, MinWidth: 4, MaxWidth: 5, Priority: 10),
        ];
    }

    // ── HTTP types ───────────────────────────────────────────────────────

    private static DisplayProfile CreateHttpMethodProfile()
    {
        return DisplayProfile
            .For<HttpMethod>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((HttpMethod)context.Value).Method);
    }

    private static DisplayProfile CreateHttpStatusCodeProfile()
    {
        return DisplayProfile
            .For<HttpStatusCode>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildHttpStatusCodeColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var code = (HttpStatusCode)context.Value;
                    return $"{(int)code} {code}";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpStatusCodeColumns()
    {
        return
        [
            new DisplayTableColumn("Code", row => (int)(HttpStatusCode)row, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 0, CanHide: false),
            new DisplayTableColumn("Name", row => ((HttpStatusCode)row).ToString(), MinWidth: 4, MaxWidth: 32, Priority: 10, CanHide: false),
        ];
    }

    private static DisplayProfile CreateMediaTypeHeaderValueProfile()
    {
        return DisplayProfile
            .For<MediaTypeHeaderValue>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildMediaTypeHeaderValueColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((MediaTypeHeaderValue)context.Value).ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildMediaTypeHeaderValueColumns()
    {
        return
        [
            new DisplayTableColumn("MediaType", row => ((MediaTypeHeaderValue)row).MediaType, MinWidth: 4, MaxWidth: 64, Priority: 0, CanHide: false),
            new DisplayTableColumn("CharSet", row => NullIfEmpty(((MediaTypeHeaderValue)row).CharSet), MinWidth: 3, MaxWidth: 24, Priority: 10),
        ];
    }

    private static DisplayProfile CreateAuthenticationHeaderValueProfile()
    {
        return DisplayProfile
            .For<AuthenticationHeaderValue>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildAuthenticationHeaderValueColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((AuthenticationHeaderValue)context.Value).ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildAuthenticationHeaderValueColumns()
    {
        return
        [
            new DisplayTableColumn("Scheme", row => ((AuthenticationHeaderValue)row).Scheme, MinWidth: 3, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Parameter", row => NullIfEmpty(((AuthenticationHeaderValue)row).Parameter), MinWidth: 4, MaxWidth: 96, Priority: 10),
        ];
    }

    private static DisplayProfile CreateContentDispositionHeaderValueProfile()
    {
        return DisplayProfile
            .For<ContentDispositionHeaderValue>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildContentDispositionHeaderValueColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ContentDispositionHeaderValue)context.Value).ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildContentDispositionHeaderValueColumns()
    {
        return
        [
            new DisplayTableColumn("DispositionType", row => ((ContentDispositionHeaderValue)row).DispositionType, MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("FileName", row => NullIfEmpty(((ContentDispositionHeaderValue)row).FileName), MinWidth: 3, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("Size", row => ((ContentDispositionHeaderValue)row).Size, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 20),
        ];
    }

    private static DisplayProfile CreateEntityTagHeaderValueProfile()
    {
        return DisplayProfile
            .For<EntityTagHeaderValue>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildEntityTagHeaderValueColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((EntityTagHeaderValue)context.Value).ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildEntityTagHeaderValueColumns()
    {
        return
        [
            new DisplayTableColumn("Tag", row => ((EntityTagHeaderValue)row).Tag, MinWidth: 4, MaxWidth: 64, Priority: 0, CanHide: false),
            new DisplayTableColumn("IsWeak", row => ((EntityTagHeaderValue)row).IsWeak, MinWidth: 4, MaxWidth: 5, Priority: 10),
        ];
    }

    private static DisplayProfile CreateCacheControlHeaderValueProfile()
    {
        return DisplayProfile
            .For<CacheControlHeaderValue>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCacheControlHeaderValueColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((CacheControlHeaderValue)context.Value).ToString());
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCacheControlHeaderValueColumns()
    {
        return
        [
            new DisplayTableColumn("Public", row => ((CacheControlHeaderValue)row).Public, MinWidth: 4, MaxWidth: 5, Priority: 0),
            new DisplayTableColumn("Private", row => ((CacheControlHeaderValue)row).Private, MinWidth: 4, MaxWidth: 5, Priority: 10),
            new DisplayTableColumn("NoCache", row => ((CacheControlHeaderValue)row).NoCache, MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("NoStore", row => ((CacheControlHeaderValue)row).NoStore, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("MaxAge", row => ((CacheControlHeaderValue)row).MaxAge, MinWidth: 3, MaxWidth: 12, Priority: 40),
            new DisplayTableColumn("MustRevalidate", row => ((CacheControlHeaderValue)row).MustRevalidate, MinWidth: 4, MaxWidth: 5, Priority: 50),
        ];
    }

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

    private static IReadOnlyList<DisplayTableColumn> BuildEndPointColumns()
    {
        return
        [
            new DisplayTableColumn("Value", row => FormatEndPointValue((EndPoint)row), MinWidth: 7, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Kind", row => GetReadableTypeName(row.GetType()), MinWidth: 6, MaxWidth: 20, Priority: 10),
            new DisplayTableColumn("Host", row => GetEndPointHost((EndPoint)row), MinWidth: 3, MaxWidth: 64, Priority: 20),
            new DisplayTableColumn("Port", row => GetEndPointPort((EndPoint)row), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 30),
            new DisplayTableColumn("Family", row => GetEndPointAddressFamily((EndPoint)row), MinWidth: 4, MaxWidth: 24, Priority: 40),
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

    private static IReadOnlyList<DisplayTableColumn> BuildHttpRequestMessageColumns()
    {
        return
        [
            new DisplayTableColumn("Method", row => ((HttpRequestMessage)row).Method.Method, MinWidth: 3, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("RequestUri", row => ((HttpRequestMessage)row).RequestUri?.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("Version", row => FormatHttpVersion(((HttpRequestMessage)row).Version), MinWidth: 3, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders(((HttpRequestMessage)row).Headers), MinWidth: 4, MaxWidth: 128, Priority: 30),
            new DisplayTableColumn("ContentType", row => ((HttpRequestMessage)row).Content?.Headers.ContentType?.ToString(), MinWidth: 3, MaxWidth: 64, Priority: 40),
            new DisplayTableColumn("ContentLength", row => ((HttpRequestMessage)row).Content?.Headers.ContentLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 50),
            new DisplayTableColumn("ContentHeaders", row => FormatHttpHeaders(((HttpRequestMessage)row).Content?.Headers), MinWidth: 4, MaxWidth: 128, Priority: 60),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpRequestDefinitionColumns()
    {
        return
        [
            new DisplayTableColumn("Method", row => ((HttpRequestDefinition)row).Method, MinWidth: 3, MaxWidth: 12, Priority: 0, CanHide: false),
            new DisplayTableColumn("RequestUri", row => ((HttpRequestDefinition)row).RequestUri.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("FollowRedirects", row => ((HttpRequestDefinition)row).FollowRedirects, MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("Timeout", row => ((HttpRequestDefinition)row).Timeout, MinWidth: 3, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("ContentType", row => ((HttpRequestDefinition)row).ContentType, MinWidth: 3, MaxWidth: 64, Priority: 40),
            new DisplayTableColumn("ContentLength", row => ((HttpRequestDefinition)row).ContentLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 50),
            new DisplayTableColumn("Headers", row => FormatHttpHeaderDictionary(((HttpRequestDefinition)row).Headers), MinWidth: 4, MaxWidth: 128, Priority: 60),
            new DisplayTableColumn("BodyKind", row => ((HttpRequestDefinition)row).BodyKind, MinWidth: 3, MaxWidth: 16, Priority: 70),
            new DisplayTableColumn("BodyPreview", row => ((HttpRequestDefinition)row).BodyPreview, MinWidth: 3, MaxWidth: 128, Priority: 80),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpResponseMessageColumns()
    {
        return
        [
            new DisplayTableColumn("Status", row => FormatHttpResponseStatus((HttpResponseMessage)row), MinWidth: 3, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("RequestUri", row => ((HttpResponseMessage)row).RequestMessage?.RequestUri?.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("Version", row => FormatHttpVersion(((HttpResponseMessage)row).Version), MinWidth: 3, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("IsSuccess", row => ((HttpResponseMessage)row).IsSuccessStatusCode, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders(((HttpResponseMessage)row).Headers), MinWidth: 4, MaxWidth: 128, Priority: 40),
            new DisplayTableColumn("ContentType", row => ((HttpResponseMessage)row).Content?.Headers.ContentType?.ToString(), MinWidth: 3, MaxWidth: 64, Priority: 50),
            new DisplayTableColumn("ContentLength", row => ((HttpResponseMessage)row).Content?.Headers.ContentLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 60),
            new DisplayTableColumn("ContentHeaders", row => FormatHttpHeaders(((HttpResponseMessage)row).Content?.Headers), MinWidth: 4, MaxWidth: 128, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpResponseInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Status", row => ((HttpResponseInfo)row).Status, MinWidth: 3, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Method", row => ((HttpResponseInfo)row).Method, MinWidth: 3, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("RequestUri", row => ((HttpResponseInfo)row).RequestUri?.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 20),
            new DisplayTableColumn("FinalUri", row => ((HttpResponseInfo)row).FinalUri?.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 30),
            new DisplayTableColumn("Version", row => ((HttpResponseInfo)row).Version, MinWidth: 3, MaxWidth: 12, Priority: 40),
            new DisplayTableColumn("IsSuccess", row => ((HttpResponseInfo)row).IsSuccess, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("ContentType", row => ((HttpResponseInfo)row).ContentType, MinWidth: 3, MaxWidth: 64, Priority: 60),
            new DisplayTableColumn("ContentLength", row => ((HttpResponseInfo)row).ContentLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Duration", row => ((HttpResponseInfo)row).Duration, MinWidth: 3, MaxWidth: 24, Priority: 80),
            new DisplayTableColumn("Headers", row => FormatHttpHeaderDictionary(((HttpResponseInfo)row).Headers), MinWidth: 4, MaxWidth: 128, Priority: 90),
            new DisplayTableColumn("ContentHeaders", row => FormatHttpHeaderDictionary(((HttpResponseInfo)row).ContentHeaders), MinWidth: 4, MaxWidth: 128, Priority: 100),
            new DisplayTableColumn("Body", row => FormatDisplaySummaryValue(((HttpResponseInfo)row).Body), MinWidth: 3, MaxWidth: 128, Priority: 110),
            new DisplayTableColumn("SavedTo", row => ((HttpResponseInfo)row).SavedTo, MinWidth: 3, MaxWidth: 128, Priority: 120),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpFileServerHandleColumns()
    {
        return
        [
            new DisplayTableColumn("#", row => ((HttpFileServerHandle)row).Id, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false),
            new DisplayTableColumn("Open", row => ((HttpFileServerHandle)row).IsOpen, MinWidth: 4, MaxWidth: 5, Priority: 10),
            new DisplayTableColumn("Url", row => ((HttpFileServerHandle)row).Url.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 20, CanHide: false),
            new DisplayTableColumn("ShareUrl", row => ((HttpFileServerHandle)row).ShareUrl.ToString(), MinWidth: 8, MaxWidth: 128, Priority: 25),
            new DisplayTableColumn("Protected", row => ((HttpFileServerHandle)row).RequiresToken, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("Bind", row => ((HttpFileServerHandle)row).BindAddress, MinWidth: 4, MaxWidth: 24, Priority: 40),
            new DisplayTableColumn("Port", row => ((HttpFileServerHandle)row).Port, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 50),
            new DisplayTableColumn("Browse", row => ((HttpFileServerHandle)row).DirectoryBrowsingEnabled, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Upload", row => ((HttpFileServerHandle)row).UploadEnabled, MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("Once", row => ((HttpFileServerHandle)row).ServeOnce, MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("Requests", row => ((HttpFileServerHandle)row).RequestCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 90),
            new DisplayTableColumn("Root", row => ((HttpFileServerHandle)row).RootPath, MinWidth: 8, MaxWidth: 128, Priority: 100),
            new DisplayTableColumn("Started", row => ((HttpFileServerHandle)row).StartedAt, MinWidth: 8, MaxWidth: 32, Priority: 110),
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

    private static IReadOnlyList<DisplayTableColumn> BuildCookieColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((Cookie)row).Name, MinWidth: 3, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Value", row => ((Cookie)row).Value, MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("Domain", row => NullIfEmpty(((Cookie)row).Domain), MinWidth: 3, MaxWidth: 48, Priority: 20),
            new DisplayTableColumn("Path", row => NullIfEmpty(((Cookie)row).Path), MinWidth: 1, MaxWidth: 48, Priority: 30),
            new DisplayTableColumn("Expires", row => ((Cookie)row).Expires == DateTime.MinValue ? null : ((Cookie)row).Expires, MinWidth: 3, MaxWidth: 32, Priority: 40),
            new DisplayTableColumn("Secure", row => ((Cookie)row).Secure, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("HttpOnly", row => ((Cookie)row).HttpOnly, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Discard", row => ((Cookie)row).Discard, MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("Expired", row => ((Cookie)row).Expired, MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("Version", row => ((Cookie)row).Version, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 90),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCookieCollectionColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((CookieCollection)row).Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Cookies", row => FormatCookieCollectionItems((CookieCollection)row), MinWidth: 8, MaxWidth: 160, Priority: 10),
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

    private static IReadOnlyList<DisplayTableColumn> BuildCookieContainerColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((CookieContainer)row).Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Capacity", row => ((CookieContainer)row).Capacity, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("PerDomainCapacity", row => ((CookieContainer)row).PerDomainCapacity, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("MaxCookieSize", row => ((CookieContainer)row).MaxCookieSize, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildNetworkCredentialColumns()
    {
        return
        [
            new DisplayTableColumn("UserName", row => NullIfEmpty(((NetworkCredential)row).UserName), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Domain", row => NullIfEmpty(((NetworkCredential)row).Domain), MinWidth: 3, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("HasPassword", row => !string.IsNullOrEmpty(((NetworkCredential)row).Password), MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("HasSecurePassword", row => SafeHasSecurePassword((NetworkCredential)row), MinWidth: 4, MaxWidth: 5, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildPhysicalAddressColumns()
    {
        return
        [
            new DisplayTableColumn("Address", row => FormatPhysicalAddressValue((PhysicalAddress)row), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Bytes", row => string.Join(" ", ((PhysicalAddress)row).GetAddressBytes().Select(value => value.ToString("X2", CultureInfo.InvariantCulture))), MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("Length", row => ((PhysicalAddress)row).GetAddressBytes().Length, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("Empty", row => ((PhysicalAddress)row).Equals(PhysicalAddress.None), MinWidth: 4, MaxWidth: 5, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpHostEntryColumns()
    {
        return
        [
            new DisplayTableColumn("HostName", row => ((IPHostEntry)row).HostName, MinWidth: 3, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Aliases", row => FormatStringCollection(((IPHostEntry)row).Aliases), MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("AddressCount", row => ((IPHostEntry)row).AddressList.Length, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("IPv4", row => FormatIpAddressCollection(((IPHostEntry)row).AddressList.Where(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)), MinWidth: 3, MaxWidth: 96, Priority: 30),
            new DisplayTableColumn("IPv6", row => FormatIpAddressCollection(((IPHostEntry)row).AddressList.Where(static address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)), MinWidth: 3, MaxWidth: 96, Priority: 40),
            new DisplayTableColumn("Addresses", row => FormatIpAddressCollection(((IPHostEntry)row).AddressList), MinWidth: 3, MaxWidth: 128, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildWebHeaderCollectionColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((WebHeaderCollection)row).Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Keys", row => FormatStringCollection(((WebHeaderCollection)row).AllKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).Cast<string>()), MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("Headers", row => FormatWebHeaderCollectionEntries((WebHeaderCollection)row), MinWidth: 8, MaxWidth: 160, Priority: 20),
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

    private static IReadOnlyList<DisplayTableColumn> BuildNetworkInterfaceColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => NullIfEmpty(((NetworkInterface)row).Name) ?? "<unnamed>", MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Description", row => NullIfEmpty(((NetworkInterface)row).Description), MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("Type", row => ((NetworkInterface)row).NetworkInterfaceType, MinWidth: 3, MaxWidth: 32, Priority: 20),
            new DisplayTableColumn("Status", row => ((NetworkInterface)row).OperationalStatus, MinWidth: 3, MaxWidth: 16, Priority: 30),
            new DisplayTableColumn("Speed", row => FormatNetworkSpeed(((NetworkInterface)row).Speed), MinWidth: 3, MaxWidth: 24, Priority: 40),
            new DisplayTableColumn("PhysicalAddress", row => FormatPhysicalAddressValue(((NetworkInterface)row).GetPhysicalAddress()), MinWidth: 3, MaxWidth: 48, Priority: 50),
            new DisplayTableColumn("IPv4", row => SafeSupportsComponent((NetworkInterface)row, NetworkInterfaceComponent.IPv4), MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("IPv6", row => SafeSupportsComponent((NetworkInterface)row, NetworkInterfaceComponent.IPv6), MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("Multicast", row => ((NetworkInterface)row).SupportsMulticast, MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("ReceiveOnly", row => ((NetworkInterface)row).IsReceiveOnly, MinWidth: 4, MaxWidth: 5, Priority: 90),
            new DisplayTableColumn("Addresses", row => FormatNetworkInterfaceAddresses((NetworkInterface)row), MinWidth: 3, MaxWidth: 128, Priority: 100),
            new DisplayTableColumn("Gateways", row => FormatNetworkInterfaceGateways((NetworkInterface)row), MinWidth: 3, MaxWidth: 96, Priority: 110),
            new DisplayTableColumn("DnsServers", row => FormatNetworkInterfaceDnsServers((NetworkInterface)row), MinWidth: 3, MaxWidth: 96, Priority: 120),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildProcessStartInfoColumns()
    {
        return
        [
            new DisplayTableColumn("FileName", row => ((ProcessStartInfo)row).FileName, MinWidth: 3, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Arguments", row => NullIfEmpty(((ProcessStartInfo)row).Arguments), MinWidth: 3, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("WorkingDirectory", row => NullIfEmpty(((ProcessStartInfo)row).WorkingDirectory), MinWidth: 3, MaxWidth: 96, Priority: 20),
            new DisplayTableColumn("Verb", row => NullIfEmpty(((ProcessStartInfo)row).Verb), MinWidth: 3, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("UseShellExecute", row => ((ProcessStartInfo)row).UseShellExecute, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("CreateNoWindow", row => ((ProcessStartInfo)row).CreateNoWindow, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("RedirectStdIn", row => ((ProcessStartInfo)row).RedirectStandardInput, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("RedirectStdOut", row => ((ProcessStartInfo)row).RedirectStandardOutput, MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("RedirectStdErr", row => ((ProcessStartInfo)row).RedirectStandardError, MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("Environment", row => ((ProcessStartInfo)row).Environment.Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 90),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildProcessModuleColumns()
    {
        return
        [
            new DisplayTableColumn("ModuleName", row => NullIfEmpty(((ProcessModule)row).ModuleName), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("FileName", row => NullIfEmpty(((ProcessModule)row).FileName), MinWidth: 3, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("MemorySize", row => StorageSize.FromBytes(((ProcessModule)row).ModuleMemorySize), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("BaseAddress", row => FormatPointer(((ProcessModule)row).BaseAddress), MinWidth: 3, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("EntryPoint", row => FormatPointer(((ProcessModule)row).EntryPointAddress), MinWidth: 3, MaxWidth: 24, Priority: 40),
            new DisplayTableColumn("FileVersion", row => SafeGetProcessModuleFileVersion((ProcessModule)row), MinWidth: 3, MaxWidth: 48, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemWatcherColumns()
    {
        return
        [
            new DisplayTableColumn("Path", row => NullIfEmpty(((FileSystemWatcher)row).Path), MinWidth: 3, MaxWidth: 128, Priority: 0, CanHide: false),
            new DisplayTableColumn("Filter", row => NullIfEmpty(((FileSystemWatcher)row).Filter), MinWidth: 1, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("Filters", row => FormatStringCollection(((FileSystemWatcher)row).Filters), MinWidth: 3, MaxWidth: 96, Priority: 20),
            new DisplayTableColumn("NotifyFilter", row => ((FileSystemWatcher)row).NotifyFilter, MinWidth: 3, MaxWidth: 64, Priority: 30),
            new DisplayTableColumn("Enabled", row => ((FileSystemWatcher)row).EnableRaisingEvents, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("IncludeSubdirectories", row => ((FileSystemWatcher)row).IncludeSubdirectories, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("InternalBufferSize", row => StorageSize.FromBytes(((FileSystemWatcher)row).InternalBufferSize), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 60),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpRequestHeadersColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((HttpRequestHeaders)row).Count(), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Host", row => NullIfEmpty(((HttpRequestHeaders)row).Host), MinWidth: 3, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("UserAgent", row => FormatProductInfoHeader(((HttpRequestHeaders)row).UserAgent), MinWidth: 3, MaxWidth: 96, Priority: 20),
            new DisplayTableColumn("Accept", row => FormatCollectionHeader(((HttpRequestHeaders)row).Accept), MinWidth: 3, MaxWidth: 96, Priority: 30),
            new DisplayTableColumn("Authorization", row => ((HttpRequestHeaders)row).Authorization?.ToString(), MinWidth: 3, MaxWidth: 96, Priority: 40),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders((HttpRequestHeaders)row), MinWidth: 8, MaxWidth: 160, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpResponseHeadersColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((HttpResponseHeaders)row).Count(), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Server", row => FormatProductInfoHeader(((HttpResponseHeaders)row).Server), MinWidth: 3, MaxWidth: 96, Priority: 10),
            new DisplayTableColumn("Date", row => ((HttpResponseHeaders)row).Date, MinWidth: 3, MaxWidth: 32, Priority: 20),
            new DisplayTableColumn("Location", row => ((HttpResponseHeaders)row).Location?.ToString(), MinWidth: 3, MaxWidth: 96, Priority: 30),
            new DisplayTableColumn("ETag", row => ((HttpResponseHeaders)row).ETag?.ToString(), MinWidth: 3, MaxWidth: 64, Priority: 40),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders((HttpResponseHeaders)row), MinWidth: 8, MaxWidth: 160, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpContentHeadersColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((HttpContentHeaders)row).Count(), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("ContentType", row => ((HttpContentHeaders)row).ContentType?.ToString(), MinWidth: 3, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("ContentLength", row => ((HttpContentHeaders)row).ContentLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("ContentEncoding", row => FormatStringCollection(((HttpContentHeaders)row).ContentEncoding), MinWidth: 3, MaxWidth: 64, Priority: 30),
            new DisplayTableColumn("ContentDisposition", row => ((HttpContentHeaders)row).ContentDisposition?.ToString(), MinWidth: 3, MaxWidth: 96, Priority: 40),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders((HttpContentHeaders)row), MinWidth: 8, MaxWidth: 160, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpHeadersColumns()
    {
        return
        [
            new DisplayTableColumn("Count", row => ((HttpHeaders)row).Count(), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders((HttpHeaders)row), MinWidth: 8, MaxWidth: 160, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildHttpContentColumns()
    {
        return
        [
            new DisplayTableColumn("Kind", row => GetReadableTypeName(row.GetType()), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("ContentType", row => ((HttpContent)row).Headers.ContentType?.ToString(), MinWidth: 3, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("ContentLength", row => ((HttpContent)row).Headers.ContentLength, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("Headers", row => FormatHttpHeaders(((HttpContent)row).Headers), MinWidth: 4, MaxWidth: 128, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpAddressColumns()
    {
        return
        [
            new DisplayTableColumn("Address", row => ((IPAddress)row).ToString(), MinWidth: 7, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Family", row => ((IPAddress)row).AddressFamily, MinWidth: 4, MaxWidth: 22, Priority: 10, CanHide: false),
            new DisplayTableColumn("Bytes", row => string.Join(".", ((IPAddress)row).GetAddressBytes()), MinWidth: 7, MaxWidth: 64, Priority: 20),
            new DisplayTableColumn("Loopback", row => IPAddress.IsLoopback((IPAddress)row), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("IPv4", row => ((IPAddress)row).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("IPv6", row => ((IPAddress)row).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("ScopeId", row => ((IPAddress)row).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? ((IPAddress)row).ScopeId : null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 60),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCommandTimingInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Elapsed", row => ((CommandTimingInfo)row).Elapsed, MinWidth: 8, MaxWidth: 20, Priority: 0, CanHide: false),
            new DisplayTableColumn("User CPU", row => ((CommandTimingInfo)row).UserCpuTime, MinWidth: 8, MaxWidth: 20, Priority: 10, CanHide: false),
            new DisplayTableColumn("System CPU", row => ((CommandTimingInfo)row).SystemCpuTime, MinWidth: 8, MaxWidth: 20, Priority: 20, CanHide: false),
            new DisplayTableColumn("CPU %", row => ((CommandTimingInfo)row).CpuPercent, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 10, Priority: 30, CanHide: false),
            new DisplayTableColumn("Peak Memory", row => ((CommandTimingInfo)row).PeakWorkingSet, MinWidth: 8, MaxWidth: 16, Priority: 40),
            new DisplayTableColumn("Memory Δ", row => ((CommandTimingInfo)row).WorkingSetDelta, MinWidth: 8, MaxWidth: 16, Priority: 50),
            new DisplayTableColumn("Allocations", row => ((CommandTimingInfo)row).ThreadAllocations, MinWidth: 8, MaxWidth: 16, Priority: 60),
            new DisplayTableColumn("Minor Faults", row => ((CommandTimingInfo)row).MinorPageFaults, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Major Faults", row => ((CommandTimingInfo)row).MajorPageFaults, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 80),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildManagedFileHandleColumns()
    {
        return
        [
            new DisplayTableColumn("#", row => ((ManagedFileHandle)row).Id, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false),
            new DisplayTableColumn("Name", row => ((ManagedFileHandle)row).Name, MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Kind", row => ((ManagedFileHandle)row).Kind, MinWidth: 4, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("Mode", row => ((ManagedFileHandle)row).Mode, MinWidth: 4, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("Open", row => ((ManagedFileHandle)row).IsOpen, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("CanRead", row => ((ManagedFileHandle)row).CanRead, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("CanWrite", row => ((ManagedFileHandle)row).CanWrite, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("CanSeek", row => ((ManagedFileHandle)row).CanSeek, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Position", row => ((ManagedFileHandle)row).Position, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Length", row => ((ManagedFileHandle)row).Length, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 80),
            new DisplayTableColumn("Encoding", row => ((ManagedFileHandle)row).Encoding, MinWidth: 4, MaxWidth: 18, Priority: 90),
            new DisplayTableColumn("Path", row => ((ManagedFileHandle)row).Path, MinWidth: 12, MaxWidth: 72, Priority: 100),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpAddressInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Family", row => ((IpAddressInfo)row).Family, MinWidth: 4, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Address", row => ((IpAddressInfo)row).Cidr, MinWidth: 9, MaxWidth: 48, Priority: 10, CanHide: false),
            new DisplayTableColumn("Scope", row => ((IpAddressInfo)row).Scope, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("Label", row => ((IpAddressInfo)row).Label, MinWidth: 2, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("Broadcast", row => ((IpAddressInfo)row).Broadcast, MinWidth: 7, MaxWidth: 48, Priority: 40),
            new DisplayTableColumn("Dynamic", row => ((IpAddressInfo)row).Dynamic, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("NoPrefixRoute", row => ((IpAddressInfo)row).NoPrefixRoute, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("ValidLifetime", row => ((IpAddressInfo)row).ValidLifetime, MinWidth: 6, MaxWidth: 16, Priority: 70),
            new DisplayTableColumn("PreferredLifetime", row => ((IpAddressInfo)row).PreferredLifetime, MinWidth: 6, MaxWidth: 16, Priority: 80),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpInterfaceColumns()
    {
        return
        [
            new DisplayTableColumn("Index", row => ((IpInterfaceInfo)row).Index, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false),
            new DisplayTableColumn("Name", row => ((IpInterfaceInfo)row).Name, MinWidth: 2, MaxWidth: 24, Priority: 10, CanHide: false),
            new DisplayTableColumn("State", row => ((IpInterfaceInfo)row).State, MinWidth: 2, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("IPv4", row => ((IpInterfaceInfo)row).IPv4, MinWidth: 7, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("IPv6", row => ((IpInterfaceInfo)row).IPv6, MinWidth: 7, MaxWidth: 48, Priority: 40),
            new DisplayTableColumn("Mtu", row => ((IpInterfaceInfo)row).Mtu, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 50),
            new DisplayTableColumn("LinkType", row => ((IpInterfaceInfo)row).LinkType, MinWidth: 3, MaxWidth: 16, Priority: 60),
            new DisplayTableColumn("MAC", row => ((IpInterfaceInfo)row).HardwareAddress, MinWidth: 8, MaxWidth: 24, Priority: 70),
            new DisplayTableColumn("AltNames", row => ((IpInterfaceInfo)row).AltNamesText, MinWidth: 4, MaxWidth: 36, Priority: 80),
            new DisplayTableColumn("Flags", row => ((IpInterfaceInfo)row).FlagsText, MinWidth: 8, MaxWidth: 48, Priority: 90),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpRouteColumns()
    {
        return
        [
            new DisplayTableColumn("Destination", row => ((IpRouteInfo)row).Destination, MinWidth: 6, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Gateway", row => ((IpRouteInfo)row).Gateway, MinWidth: 7, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("Device", row => ((IpRouteInfo)row).Device, MinWidth: 4, MaxWidth: 24, Priority: 20),
            new DisplayTableColumn("Protocol", row => ((IpRouteInfo)row).Protocol, MinWidth: 4, MaxWidth: 16, Priority: 30),
            new DisplayTableColumn("Scope", row => ((IpRouteInfo)row).Scope, MinWidth: 3, MaxWidth: 12, Priority: 40),
            new DisplayTableColumn("PrefSrc", row => ((IpRouteInfo)row).PreferredSource, MinWidth: 7, MaxWidth: 48, Priority: 50),
            new DisplayTableColumn("Metric", row => ((IpRouteInfo)row).Metric, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 60),
            new DisplayTableColumn("Pref", row => ((IpRouteInfo)row).Preference, MinWidth: 3, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Table", row => ((IpRouteInfo)row).Table, MinWidth: 3, MaxWidth: 16, Priority: 80),
            new DisplayTableColumn("Type", row => ((IpRouteInfo)row).RouteType, MinWidth: 3, MaxWidth: 16, Priority: 90),
            new DisplayTableColumn("Flags", row => ((IpRouteInfo)row).FlagsText, MinWidth: 4, MaxWidth: 24, Priority: 100),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpNeighborColumns()
    {
        return
        [
            new DisplayTableColumn("Address", row => ((IpNeighborInfo)row).Address, MinWidth: 7, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Device", row => ((IpNeighborInfo)row).Device, MinWidth: 3, MaxWidth: 24, Priority: 10),
            new DisplayTableColumn("MAC", row => ((IpNeighborInfo)row).LinkLayerAddress, MinWidth: 8, MaxWidth: 24, Priority: 20),
            new DisplayTableColumn("State", row => ((IpNeighborInfo)row).StateText, MinWidth: 4, MaxWidth: 16, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpRuleColumns()
    {
        return
        [
            new DisplayTableColumn("Priority", row => ((IpRuleInfo)row).Priority, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 0, CanHide: false),
            new DisplayTableColumn("Source", row => ((IpRuleInfo)row).SourceText, MinWidth: 3, MaxWidth: 32, Priority: 10),
            new DisplayTableColumn("Destination", row => ((IpRuleInfo)row).DestinationText, MinWidth: 3, MaxWidth: 32, Priority: 20),
            new DisplayTableColumn("Table", row => ((IpRuleInfo)row).Table, MinWidth: 3, MaxWidth: 16, Priority: 30),
            new DisplayTableColumn("Action", row => ((IpRuleInfo)row).Action, MinWidth: 3, MaxWidth: 16, Priority: 40),
            new DisplayTableColumn("Protocol", row => ((IpRuleInfo)row).Protocol, MinWidth: 3, MaxWidth: 16, Priority: 50),
            new DisplayTableColumn("IifName", row => ((IpRuleInfo)row).IifName, MinWidth: 3, MaxWidth: 16, Priority: 60),
            new DisplayTableColumn("OifName", row => ((IpRuleInfo)row).OifName, MinWidth: 3, MaxWidth: 16, Priority: 70),
            new DisplayTableColumn("FwMark", row => ((IpRuleInfo)row).FirewallMark, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 80),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpNetnsColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((IpNetnsInfo)row).Name, MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Id", row => ((IpNetnsInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 10, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpTunnelColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((IpTunnelInfo)row).Name, MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Mode", row => ((IpTunnelInfo)row).Mode, MinWidth: 3, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("Remote", row => ((IpTunnelInfo)row).Remote, MinWidth: 7, MaxWidth: 48, Priority: 20),
            new DisplayTableColumn("Local", row => ((IpTunnelInfo)row).Local, MinWidth: 7, MaxWidth: 48, Priority: 30),
            new DisplayTableColumn("Ttl", row => ((IpTunnelInfo)row).Ttl, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 40),
            new DisplayTableColumn("Tos", row => ((IpTunnelInfo)row).Tos, MinWidth: 3, MaxWidth: 12, Priority: 50),
            new DisplayTableColumn("Dev", row => ((IpTunnelInfo)row).Dev, MinWidth: 3, MaxWidth: 16, Priority: 60),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpTuntapColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((IpTuntapInfo)row).Name, MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Mode", row => ((IpTuntapInfo)row).Mode, MinWidth: 3, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("User", row => ((IpTuntapInfo)row).User, MinWidth: 3, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("Group", row => ((IpTuntapInfo)row).Group, MinWidth: 3, MaxWidth: 16, Priority: 30),
            new DisplayTableColumn("MultiQueue", row => ((IpTuntapInfo)row).MultiQueue, MinWidth: 3, MaxWidth: 8, Priority: 40),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpVrfColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((IpVrfInfo)row).Name, MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("TableId", row => ((IpVrfInfo)row).TableId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpMaddrColumns()
    {
        return
        [
            new DisplayTableColumn("Index", row => ((IpMaddrInfo)row).Index, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false),
            new DisplayTableColumn("Name", row => ((IpMaddrInfo)row).Name, MinWidth: 3, MaxWidth: 24, Priority: 10, CanHide: false),
            new DisplayTableColumn("Addrs", row => ((IpMaddrInfo)row).AddressCount, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 20),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpMaddrEntryColumns()
    {
        return
        [
            new DisplayTableColumn("Family", row => ((IpMaddrEntry)row).Family, MinWidth: 3, MaxWidth: 8, Priority: 0),
            new DisplayTableColumn("Address", row => ((IpMaddrEntry)row).Address, MinWidth: 7, MaxWidth: 48, Priority: 10, CanHide: false),
            new DisplayTableColumn("Link", row => ((IpMaddrEntry)row).Link, MinWidth: 8, MaxWidth: 24, Priority: 20),
            new DisplayTableColumn("Users", row => ((IpMaddrEntry)row).Users, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 30),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpMrouteColumns()
    {
        return
        [
            new DisplayTableColumn("Group", row => ((IpMrouteInfo)row).Group, MinWidth: 7, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Source", row => ((IpMrouteInfo)row).Source, MinWidth: 7, MaxWidth: 48, Priority: 10),
            new DisplayTableColumn("Iif", row => ((IpMrouteInfo)row).Iif, MinWidth: 3, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("Oifs", row => string.Join(", ", ((IpMrouteInfo)row).Oifs), MinWidth: 3, MaxWidth: 32, Priority: 30),
            new DisplayTableColumn("Packets", row => ((IpMrouteInfo)row).Packets, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 40),
            new DisplayTableColumn("Bytes", row => ((IpMrouteInfo)row).Bytes, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpTokenColumns()
    {
        return
        [
            new DisplayTableColumn("Token", row => ((IpTokenInfo)row).Token, MinWidth: 4, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Interface", row => ((IpTokenInfo)row).InterfaceName, MinWidth: 3, MaxWidth: 24, Priority: 10),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildIpNtableColumns()
    {
        return
        [
            new DisplayTableColumn("Family", row => ((IpNtableInfo)row).Family, MinWidth: 4, MaxWidth: 8, Priority: 0, CanHide: false),
            new DisplayTableColumn("Name", row => ((IpNtableInfo)row).Name, MinWidth: 4, MaxWidth: 16, Priority: 10, CanHide: false),
            new DisplayTableColumn("Dev", row => ((IpNtableInfo)row).Dev, MinWidth: 3, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("Reachable", row => ((IpNtableInfo)row).Reachable, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 30),
            new DisplayTableColumn("BaseReach", row => ((IpNtableInfo)row).BaseReachable, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 40),
            new DisplayTableColumn("Retrans", row => ((IpNtableInfo)row).Retrans, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 50),
            new DisplayTableColumn("GcStale", row => ((IpNtableInfo)row).GcStale, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 60),
            new DisplayTableColumn("RefCnt", row => ((IpNtableInfo)row).RefCount, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 70),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdUnitInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Unit", row => ((SystemdUnitInfo)row).Unit, MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "UNIT"),
            new DisplayTableColumn("Load", row => ((SystemdUnitInfo)row).LoadState, MinWidth: 4, MaxWidth: 12, Priority: 10, SelectionKey: "LOAD"),
            new DisplayTableColumn("Active", row => ((SystemdUnitInfo)row).ActiveState, MinWidth: 5, MaxWidth: 12, Priority: 20, SelectionKey: "ACTIVE"),
            new DisplayTableColumn("Sub", row => ((SystemdUnitInfo)row).SubState, MinWidth: 3, MaxWidth: 16, Priority: 30, SelectionKey: "SUB"),
            new DisplayTableColumn("Type", row => ((SystemdUnitInfo)row).UnitType, MinWidth: 4, MaxWidth: 16, Priority: 40, SelectionKey: "TYPE"),
            new DisplayTableColumn("Description", row => ((SystemdUnitInfo)row).Description, MinWidth: 10, MaxWidth: 64, Priority: 50, SelectionKey: "DESCRIPTION"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdUnitFileInfoColumns()
    {
        return
        [
            new DisplayTableColumn("UnitFile", row => ((SystemdUnitFileInfo)row).UnitFile, MinWidth: 8, MaxWidth: 56, Priority: 0, CanHide: false, SelectionKey: "UNIT_FILE"),
            new DisplayTableColumn("Type", row => ((SystemdUnitFileInfo)row).UnitType, MinWidth: 4, MaxWidth: 16, Priority: 10, SelectionKey: "TYPE"),
            new DisplayTableColumn("State", row => ((SystemdUnitFileInfo)row).State, MinWidth: 4, MaxWidth: 20, Priority: 20, SelectionKey: "STATE"),
            new DisplayTableColumn("Preset", row => ((SystemdUnitFileInfo)row).Preset, MinWidth: 4, MaxWidth: 20, Priority: 30, SelectionKey: "PRESET"),
            new DisplayTableColumn("Enabled", row => ((SystemdUnitFileInfo)row).IsEnabled, MinWidth: 4, MaxWidth: 8, Priority: 40, SelectionKey: "ENABLED"),
            new DisplayTableColumn("Masked", row => ((SystemdUnitFileInfo)row).IsMasked, MinWidth: 4, MaxWidth: 8, Priority: 50, SelectionKey: "MASKED"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdUnitPropertySetColumns()
    {
        return
        [
            new DisplayTableColumn("Id", row => ((SystemdUnitPropertySet)row).Id, MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "Id"),
            new DisplayTableColumn("Description", row => ((SystemdUnitPropertySet)row).Description, MinWidth: 10, MaxWidth: 64, Priority: 10, SelectionKey: "Description"),
            new DisplayTableColumn("LoadState", row => ((SystemdUnitPropertySet)row).LoadState, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "LoadState"),
            new DisplayTableColumn("ActiveState", row => ((SystemdUnitPropertySet)row).ActiveState, MinWidth: 5, MaxWidth: 12, Priority: 30, SelectionKey: "ActiveState"),
            new DisplayTableColumn("SubState", row => ((SystemdUnitPropertySet)row).SubState, MinWidth: 3, MaxWidth: 16, Priority: 40, SelectionKey: "SubState"),
            new DisplayTableColumn("UnitFileState", row => ((SystemdUnitPropertySet)row).UnitFileState, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "UnitFileState"),
            new DisplayTableColumn("Type", row => ((SystemdUnitPropertySet)row).Type, MinWidth: 4, MaxWidth: 16, Priority: 60, SelectionKey: "Type"),
            new DisplayTableColumn("MainPID", row => ((SystemdUnitPropertySet)row).MainPid, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 70, SelectionKey: "MainPID"),
            new DisplayTableColumn("TasksCurrent", row => ((SystemdUnitPropertySet)row).TasksCurrent, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 80, SelectionKey: "TasksCurrent"),
            new DisplayTableColumn("MemoryCurrent", row => ((SystemdUnitPropertySet)row).MemoryCurrent, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 90, SelectionKey: "MemoryCurrent"),
            new DisplayTableColumn("NeedDaemonReload", row => ((SystemdUnitPropertySet)row).NeedDaemonReload, MinWidth: 4, MaxWidth: 7, Priority: 100, SelectionKey: "NeedDaemonReload"),
            new DisplayTableColumn("RecentLogCount", row => ((SystemdUnitPropertySet)row).RecentLogCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 110, SelectionKey: "RecentLogCount"),
            new DisplayTableColumn("FragmentPath", row => ((SystemdUnitPropertySet)row).FragmentPath, MinWidth: 8, MaxWidth: 72, Priority: 120, SelectionKey: "FragmentPath"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdJournalEntryDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Timestamp", row => ((SystemdJournalEntry)row).Timestamp, MinWidth: 8, MaxWidth: 36, Priority: 0, CanHide: false, SelectionKey: "__REALTIME_TIMESTAMP"),
            new DisplayTableColumn("Priority", row => ((SystemdJournalEntry)row).PriorityName, MinWidth: 4, MaxWidth: 8, Priority: 10, SelectionKey: "PRIORITY"),
            new DisplayTableColumn("Source", row => ((SystemdJournalEntry)row).Source, MinWidth: 4, MaxWidth: 32, Priority: 20, SelectionKey: "SOURCE"),
            new DisplayTableColumn("PID", row => ((SystemdJournalEntry)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 30, SelectionKey: "_PID"),
            new DisplayTableColumn("Message", row => ((SystemdJournalEntry)row).Message, MinWidth: 10, MaxWidth: 96, Priority: 40, SelectionKey: "MESSAGE"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdJournalEntrySelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Timestamp", row => ((SystemdJournalEntry)row).Timestamp, MinWidth: 8, MaxWidth: 36, Priority: 0, CanHide: false, SelectionKey: "__REALTIME_TIMESTAMP"),
            new DisplayTableColumn("Priority", row => ((SystemdJournalEntry)row).PriorityName, MinWidth: 4, MaxWidth: 8, Priority: 10, SelectionKey: "PRIORITY"),
            new DisplayTableColumn("Source", row => ((SystemdJournalEntry)row).Source, MinWidth: 4, MaxWidth: 32, Priority: 20, SelectionKey: "SOURCE"),
            new DisplayTableColumn("Unit", row => ((SystemdJournalEntry)row).Unit, MinWidth: 4, MaxWidth: 40, Priority: 30, SelectionKey: "_SYSTEMD_UNIT"),
            new DisplayTableColumn("UserUnit", row => ((SystemdJournalEntry)row).UserUnit, MinWidth: 4, MaxWidth: 40, Priority: 40, SelectionKey: "_SYSTEMD_USER_UNIT"),
            new DisplayTableColumn("Identifier", row => ((SystemdJournalEntry)row).Identifier, MinWidth: 4, MaxWidth: 32, Priority: 50, SelectionKey: "SYSLOG_IDENTIFIER"),
            new DisplayTableColumn("Comm", row => ((SystemdJournalEntry)row).Comm, MinWidth: 4, MaxWidth: 32, Priority: 60, SelectionKey: "_COMM"),
            new DisplayTableColumn("Exe", row => ((SystemdJournalEntry)row).Exe, MinWidth: 4, MaxWidth: 56, Priority: 70, SelectionKey: "_EXE"),
            new DisplayTableColumn("PID", row => ((SystemdJournalEntry)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 80, SelectionKey: "_PID"),
            new DisplayTableColumn("SyslogPID", row => ((SystemdJournalEntry)row).SyslogPid, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 10, Priority: 90, SelectionKey: "SYSLOG_PID"),
            new DisplayTableColumn("Host", row => ((SystemdJournalEntry)row).Hostname, MinWidth: 4, MaxWidth: 24, Priority: 100, SelectionKey: "_HOSTNAME"),
            new DisplayTableColumn("Transport", row => ((SystemdJournalEntry)row).Transport, MinWidth: 4, MaxWidth: 12, Priority: 110, SelectionKey: "_TRANSPORT"),
            new DisplayTableColumn("Scope", row => ((SystemdJournalEntry)row).RuntimeScope, MinWidth: 4, MaxWidth: 12, Priority: 120, SelectionKey: "_RUNTIME_SCOPE"),
            new DisplayTableColumn("InvocationID", row => ((SystemdJournalEntry)row).InvocationId, MinWidth: 8, MaxWidth: 36, Priority: 130, SelectionKey: "_SYSTEMD_INVOCATION_ID"),
            new DisplayTableColumn("Facility", row => ((SystemdJournalEntry)row).Facility, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 140, SelectionKey: "SYSLOG_FACILITY"),
            new DisplayTableColumn("CommandLine", row => ((SystemdJournalEntry)row).CommandLine, MinWidth: 8, MaxWidth: 72, Priority: 150, SelectionKey: "_CMDLINE"),
            new DisplayTableColumn("Cursor", row => ((SystemdJournalEntry)row).Cursor, MinWidth: 8, MaxWidth: 48, Priority: 160, SelectionKey: "__CURSOR"),
            new DisplayTableColumn("Message", row => ((SystemdJournalEntry)row).Message, MinWidth: 10, MaxWidth: 96, Priority: 170, SelectionKey: "MESSAGE"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdLoginSessionInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Session", row => ((SystemdLoginSessionInfo)row).Session, MinWidth: 1, MaxWidth: 12, Priority: 0, CanHide: false, SelectionKey: "SESSION"),
            new DisplayTableColumn("UID", row => ((SystemdLoginSessionInfo)row).UserId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 10, SelectionKey: "UID"),
            new DisplayTableColumn("User", row => ((SystemdLoginSessionInfo)row).User, MinWidth: 3, MaxWidth: 24, Priority: 20, SelectionKey: "USER"),
            new DisplayTableColumn("Seat", row => ((SystemdLoginSessionInfo)row).Seat, MinWidth: 4, MaxWidth: 16, Priority: 30, SelectionKey: "SEAT"),
            new DisplayTableColumn("Leader", row => ((SystemdLoginSessionInfo)row).Leader, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 40, SelectionKey: "LEADER"),
            new DisplayTableColumn("Class", row => ((SystemdLoginSessionInfo)row).Class, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "CLASS"),
            new DisplayTableColumn("TTY", row => ((SystemdLoginSessionInfo)row).Tty, MinWidth: 3, MaxWidth: 16, Priority: 60, SelectionKey: "TTY"),
            new DisplayTableColumn("Idle", row => ((SystemdLoginSessionInfo)row).Idle, MinWidth: 4, MaxWidth: 5, Priority: 70, SelectionKey: "IDLE"),
            new DisplayTableColumn("Since", row => ((SystemdLoginSessionInfo)row).Since, MinWidth: 8, MaxWidth: 36, Priority: 80, SelectionKey: "SINCE"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdLoginUserInfoColumns()
    {
        return
        [
            new DisplayTableColumn("UID", row => ((SystemdLoginUserInfo)row).UserId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false, SelectionKey: "UID"),
            new DisplayTableColumn("User", row => ((SystemdLoginUserInfo)row).User, MinWidth: 3, MaxWidth: 24, Priority: 10, SelectionKey: "USER"),
            new DisplayTableColumn("State", row => ((SystemdLoginUserInfo)row).State, MinWidth: 4, MaxWidth: 16, Priority: 20, SelectionKey: "STATE"),
            new DisplayTableColumn("Linger", row => ((SystemdLoginUserInfo)row).Linger, MinWidth: 4, MaxWidth: 6, Priority: 30, SelectionKey: "LINGER"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdLoginSeatInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Seat", row => ((SystemdLoginSeatInfo)row).Seat, MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false, SelectionKey: "SEAT"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdPropertySetColumns()
    {
        return
        [
            new DisplayTableColumn("Id", row => ((SystemdPropertySet)row).Id, MinWidth: 1, MaxWidth: 24, Priority: 0, CanHide: false, SelectionKey: "Id"),
            new DisplayTableColumn("UID", row => ((SystemdPropertySet)row).Properties.TryGetValue("UID", out var uid) ? uid : null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 10, SelectionKey: "UID"),
            new DisplayTableColumn("Name", row => ((SystemdPropertySet)row).Name, MinWidth: 3, MaxWidth: 24, Priority: 20, SelectionKey: "Name"),
            new DisplayTableColumn("User", row => ((SystemdPropertySet)row).User, MinWidth: 3, MaxWidth: 24, Priority: 30, SelectionKey: "User"),
            new DisplayTableColumn("State", row => ((SystemdPropertySet)row).State, MinWidth: 4, MaxWidth: 16, Priority: 40, SelectionKey: "State"),
            new DisplayTableColumn("Class", row => ((SystemdPropertySet)row).Class, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "Class"),
            new DisplayTableColumn("Type", row => ((SystemdPropertySet)row).Type, MinWidth: 4, MaxWidth: 16, Priority: 60, SelectionKey: "Type"),
            new DisplayTableColumn("Seat", row => ((SystemdPropertySet)row).Seat, MinWidth: 4, MaxWidth: 16, Priority: 70, SelectionKey: "Seat"),
            new DisplayTableColumn("ActiveSession", row => ((SystemdPropertySet)row).ActiveSession, MinWidth: 1, MaxWidth: 16, Priority: 80, SelectionKey: "ActiveSession"),
            new DisplayTableColumn("Service", row => ((SystemdPropertySet)row).Service, MinWidth: 4, MaxWidth: 32, Priority: 90, SelectionKey: "Service"),
            new DisplayTableColumn("Display", row => ((SystemdPropertySet)row).Display, MinWidth: 1, MaxWidth: 16, Priority: 100, SelectionKey: "Display"),
            new DisplayTableColumn("Timestamp", row => ((SystemdPropertySet)row).Timestamp, MinWidth: 8, MaxWidth: 36, Priority: 110, SelectionKey: "Timestamp"),
            new DisplayTableColumn("PropertyCount", row => ((SystemdPropertySet)row).PropertyCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 120, SelectionKey: "PropertyCount"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdHostInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Hostname", row => ((SystemdHostInfo)row).DisplayHostname, MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false, SelectionKey: "Hostname"),
            new DisplayTableColumn("StaticHostname", row => ((SystemdHostInfo)row).StaticHostname, MinWidth: 4, MaxWidth: 32, Priority: 10, SelectionKey: "StaticHostname"),
            new DisplayTableColumn("PrettyHostname", row => ((SystemdHostInfo)row).PrettyHostname, MinWidth: 4, MaxWidth: 32, Priority: 20, SelectionKey: "PrettyHostname"),
            new DisplayTableColumn("OperatingSystem", row => ((SystemdHostInfo)row).OperatingSystem, MinWidth: 6, MaxWidth: 40, Priority: 30, SelectionKey: "OperatingSystem"),
            new DisplayTableColumn("KernelRelease", row => ((SystemdHostInfo)row).KernelRelease, MinWidth: 6, MaxWidth: 32, Priority: 40, SelectionKey: "KernelRelease"),
            new DisplayTableColumn("KernelVersion", row => ((SystemdHostInfo)row).KernelVersion, MinWidth: 6, MaxWidth: 64, Priority: 50, SelectionKey: "KernelVersion"),
            new DisplayTableColumn("Chassis", row => ((SystemdHostInfo)row).Chassis, MinWidth: 4, MaxWidth: 16, Priority: 60, SelectionKey: "Chassis"),
            new DisplayTableColumn("HardwareVendor", row => ((SystemdHostInfo)row).HardwareVendor, MinWidth: 4, MaxWidth: 24, Priority: 70, SelectionKey: "HardwareVendor"),
            new DisplayTableColumn("HardwareModel", row => ((SystemdHostInfo)row).HardwareModel, MinWidth: 4, MaxWidth: 40, Priority: 80, SelectionKey: "HardwareModel"),
            new DisplayTableColumn("FirmwareVersion", row => ((SystemdHostInfo)row).FirmwareVersion, MinWidth: 4, MaxWidth: 20, Priority: 90, SelectionKey: "FirmwareVersion"),
            new DisplayTableColumn("FirmwareDate", row => ((SystemdHostInfo)row).FirmwareDate, MinWidth: 8, MaxWidth: 36, Priority: 100, SelectionKey: "FirmwareDate"),
            new DisplayTableColumn("HomeURL", row => ((SystemdHostInfo)row).OperatingSystemHomeUrl, MinWidth: 8, MaxWidth: 48, Priority: 110, SelectionKey: "OperatingSystemHomeURL"),
            new DisplayTableColumn("MachineID", row => ((SystemdHostInfo)row).MachineId, MinWidth: 8, MaxWidth: 36, Priority: 120, SelectionKey: "MachineID"),
            new DisplayTableColumn("BootID", row => ((SystemdHostInfo)row).BootId, MinWidth: 8, MaxWidth: 36, Priority: 130, SelectionKey: "BootID"),
            new DisplayTableColumn("Location", row => ((SystemdHostInfo)row).Location, MinWidth: 4, MaxWidth: 24, Priority: 140, SelectionKey: "Location"),
            new DisplayTableColumn("PropertyCount", row => ((SystemdHostInfo)row).PropertyCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 150, SelectionKey: "PropertyCount"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildSystemdNetworkLinkInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Index", row => ((SystemdNetworkLinkInfo)row).Index, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 0, CanHide: false, SelectionKey: "IDX"),
            new DisplayTableColumn("Link", row => ((SystemdNetworkLinkInfo)row).Link, MinWidth: 2, MaxWidth: 24, Priority: 10, CanHide: false, SelectionKey: "LINK"),
            new DisplayTableColumn("Type", row => ((SystemdNetworkLinkInfo)row).Type, MinWidth: 3, MaxWidth: 16, Priority: 20, SelectionKey: "TYPE"),
            new DisplayTableColumn("Operational", row => ((SystemdNetworkLinkInfo)row).OperationalState, MinWidth: 3, MaxWidth: 20, Priority: 30, SelectionKey: "OPERATIONAL"),
            new DisplayTableColumn("Setup", row => ((SystemdNetworkLinkInfo)row).SetupState, MinWidth: 3, MaxWidth: 20, Priority: 40, SelectionKey: "SETUP"),
            new DisplayTableColumn("Managed", row => ((SystemdNetworkLinkInfo)row).IsManaged, MinWidth: 4, MaxWidth: 7, Priority: 50, SelectionKey: "MANAGED"),
            new DisplayTableColumn("Configured", row => ((SystemdNetworkLinkInfo)row).IsConfigured, MinWidth: 4, MaxWidth: 10, Priority: 60, SelectionKey: "CONFIGURED"),
            new DisplayTableColumn("Routable", row => ((SystemdNetworkLinkInfo)row).IsRoutable, MinWidth: 4, MaxWidth: 8, Priority: 70, SelectionKey: "ROUTABLE"),
            new DisplayTableColumn("Carrier", row => ((SystemdNetworkLinkInfo)row).HasCarrier, MinWidth: 4, MaxWidth: 7, Priority: 80, SelectionKey: "CARRIER"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildBlockDeviceDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((BlockDeviceInfo)row).Name, MinWidth: 3, MaxWidth: 40, Priority: 0, CanHide: false, SelectionKey: "NAME", IsTree: true),
            new DisplayTableColumn("Type", row => ((BlockDeviceInfo)row).Type, MinWidth: 4, MaxWidth: 10, Priority: 10, SelectionKey: "TYPE"),
            new DisplayTableColumn("Size", row => ((BlockDeviceInfo)row).DisplaySize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 20, SelectionKey: "SIZE"),
            new DisplayTableColumn("FsType", row => ((BlockDeviceInfo)row).FileSystemType, MinWidth: 4, MaxWidth: 12, Priority: 30, SelectionKey: "FSTYPE"),
            new DisplayTableColumn("MountPoints", row => ((BlockDeviceInfo)row).MountPointsText, MinWidth: 4, MaxWidth: 36, Priority: 40, SelectionKey: "MOUNTPOINTS"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCpuInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Architecture", row => ((CpuInfo)row).Architecture, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false, SelectionKey: "ARCHITECTURE"),
            new DisplayTableColumn("ModelName", row => ((CpuInfo)row).ModelName, MinWidth: 8, MaxWidth: 48, Priority: 10, SelectionKey: "MODELNAME"),
            new DisplayTableColumn("CPU(s)", row => ((CpuInfo)row).CpuCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 20, SelectionKey: "CPU(S)"),
            new DisplayTableColumn("VendorId", row => ((CpuInfo)row).VendorId, MinWidth: 4, MaxWidth: 24, Priority: 30, SelectionKey: "VENDOR ID"),
            new DisplayTableColumn("ThreadsPerCore", row => ((CpuInfo)row).ThreadsPerCore, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 40, SelectionKey: "THREAD(S) PER CORE"),
            new DisplayTableColumn("CoresPerSocket", row => ((CpuInfo)row).CoresPerSocket, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 50, SelectionKey: "CORE(S) PER SOCKET"),
            new DisplayTableColumn("Sockets", row => ((CpuInfo)row).SocketCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 60, SelectionKey: "SOCKET(S)"),
            new DisplayTableColumn("OnlineList", row => ((CpuInfo)row).OnlineCpuList, MinWidth: 4, MaxWidth: 24, Priority: 70, SelectionKey: "ON-LINE CPU(S) LIST"),
            new DisplayTableColumn("CpuOpModes", row => ((CpuInfo)row).CpuOpModesText, MinWidth: 4, MaxWidth: 24, Priority: 80, SelectionKey: "CPU OP-MODE(S)"),
            new DisplayTableColumn("AddressSizes", row => ((CpuInfo)row).AddressSizesText, MinWidth: 8, MaxWidth: 32, Priority: 90, SelectionKey: "ADDRESS SIZES"),
            new DisplayTableColumn("PhysicalBits", row => ((CpuInfo)row).PhysicalAddressBits, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 100, SelectionKey: "PHYSICALADDRESSBITS"),
            new DisplayTableColumn("VirtualBits", row => ((CpuInfo)row).VirtualAddressBits, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 110, SelectionKey: "VIRTUALADDRESSBITS"),
            new DisplayTableColumn("ByteOrder", row => ((CpuInfo)row).ByteOrder, MinWidth: 4, MaxWidth: 16, Priority: 120, SelectionKey: "BYTE ORDER"),
            new DisplayTableColumn("CpuFamily", row => ((CpuInfo)row).CpuFamily, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 130, SelectionKey: "CPU FAMILY"),
            new DisplayTableColumn("Model", row => ((CpuInfo)row).Model, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 140, SelectionKey: "MODEL"),
            new DisplayTableColumn("Stepping", row => ((CpuInfo)row).Stepping, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 150, SelectionKey: "STEPPING"),
            new DisplayTableColumn("Boost", row => ((CpuInfo)row).FrequencyBoostText, MinWidth: 3, MaxWidth: 12, Priority: 160, SelectionKey: "FREQUENCY BOOST"),
            new DisplayTableColumn("Scaling%", row => FormatUsePercent(((CpuInfo)row).ScalingPercent), DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 170, SelectionKey: "CPU(S) SCALING MHZ"),
            new DisplayTableColumn("MaxMHz", row => ((CpuInfo)row).MaxMhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 180, SelectionKey: "CPU MAX MHZ"),
            new DisplayTableColumn("MinMHz", row => ((CpuInfo)row).MinMhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 190, SelectionKey: "CPU MIN MHZ"),
            new DisplayTableColumn("BogoMips", row => ((CpuInfo)row).BogoMips, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 200, SelectionKey: "BOGOMIPS"),
            new DisplayTableColumn("Virtualization", row => ((CpuInfo)row).Virtualization, MinWidth: 4, MaxWidth: 20, Priority: 210, SelectionKey: "VIRTUALIZATION"),
            new DisplayTableColumn("L1dCache", row => ((CpuInfo)row).L1dCache, MinWidth: 4, MaxWidth: 24, Priority: 220, SelectionKey: "L1D CACHE"),
            new DisplayTableColumn("L1iCache", row => ((CpuInfo)row).L1iCache, MinWidth: 4, MaxWidth: 24, Priority: 230, SelectionKey: "L1I CACHE"),
            new DisplayTableColumn("L2Cache", row => ((CpuInfo)row).L2Cache, MinWidth: 4, MaxWidth: 24, Priority: 240, SelectionKey: "L2 CACHE"),
            new DisplayTableColumn("L3Cache", row => ((CpuInfo)row).L3Cache, MinWidth: 4, MaxWidth: 24, Priority: 250, SelectionKey: "L3 CACHE"),
            new DisplayTableColumn("NumaNodes", row => ((CpuInfo)row).NumaNodeCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 260, SelectionKey: "NUMA NODE(S)"),
            new DisplayTableColumn("NumaNodeMap", row => ((CpuInfo)row).NumaNodesText, MinWidth: 8, MaxWidth: 48, Priority: 270, SelectionKey: "NUMANODES"),
            new DisplayTableColumn("Flags", row => ((CpuInfo)row).FlagsText, MinWidth: 8, MaxWidth: 64, Priority: 280, SelectionKey: "FLAGS"),
            new DisplayTableColumn("Vulnerabilities", row => ((CpuInfo)row).VulnerabilitiesText, MinWidth: 8, MaxWidth: 72, Priority: 290, SelectionKey: "VULNERABILITIES"),
            new DisplayTableColumn("AdditionalFields", row => ((CpuInfo)row).AdditionalFieldsText, MinWidth: 8, MaxWidth: 72, Priority: 300, SelectionKey: "ADDITIONALFIELDS"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCpuTopologyDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("CPU", row => ((CpuTopologyInfo)row).Cpu, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false, SelectionKey: "CPU"),
            new DisplayTableColumn("Node", row => ((CpuTopologyInfo)row).Node, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 10, SelectionKey: "NODE"),
            new DisplayTableColumn("Socket", row => ((CpuTopologyInfo)row).Socket, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 20, SelectionKey: "SOCKET"),
            new DisplayTableColumn("Core", row => ((CpuTopologyInfo)row).Core, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 30, SelectionKey: "CORE"),
            new DisplayTableColumn("Online", row => ((CpuTopologyInfo)row).Online, MinWidth: 4, MaxWidth: 6, Priority: 40, SelectionKey: "ONLINE"),
            new DisplayTableColumn("MHz", row => ((CpuTopologyInfo)row).Mhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 50, SelectionKey: "MHZ"),
            new DisplayTableColumn("MaxMHz", row => ((CpuTopologyInfo)row).MaxMhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 60, SelectionKey: "MAXMHZ"),
            new DisplayTableColumn("MinMHz", row => ((CpuTopologyInfo)row).MinMhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 70, SelectionKey: "MINMHZ"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCpuTopologySelectableColumns()
    {
        return
        [
            new DisplayTableColumn("CPU", row => ((CpuTopologyInfo)row).Cpu, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false, SelectionKey: "CPU"),
            new DisplayTableColumn("Node", row => ((CpuTopologyInfo)row).Node, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 10, SelectionKey: "NODE"),
            new DisplayTableColumn("Socket", row => ((CpuTopologyInfo)row).Socket, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 20, SelectionKey: "SOCKET"),
            new DisplayTableColumn("Core", row => ((CpuTopologyInfo)row).Core, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 30, SelectionKey: "CORE"),
            new DisplayTableColumn("Cluster", row => ((CpuTopologyInfo)row).Cluster, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 40, SelectionKey: "CLUSTER"),
            new DisplayTableColumn("Book", row => ((CpuTopologyInfo)row).Book, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 50, SelectionKey: "BOOK"),
            new DisplayTableColumn("Drawer", row => ((CpuTopologyInfo)row).Drawer, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 60, SelectionKey: "DRAWER"),
            new DisplayTableColumn("CacheIds", row => ((CpuTopologyInfo)row).CacheIds, MinWidth: 4, MaxWidth: 18, Priority: 70, SelectionKey: "CACHE"),
            new DisplayTableColumn("Polarization", row => ((CpuTopologyInfo)row).Polarization, MinWidth: 4, MaxWidth: 16, Priority: 80, SelectionKey: "POLARIZATION"),
            new DisplayTableColumn("Address", row => ((CpuTopologyInfo)row).Address, MinWidth: 4, MaxWidth: 16, Priority: 90, SelectionKey: "ADDRESS"),
            new DisplayTableColumn("Configured", row => ((CpuTopologyInfo)row).Configured, MinWidth: 4, MaxWidth: 12, Priority: 100, SelectionKey: "CONFIGURED"),
            new DisplayTableColumn("Online", row => ((CpuTopologyInfo)row).Online, MinWidth: 4, MaxWidth: 6, Priority: 110, SelectionKey: "ONLINE"),
            new DisplayTableColumn("MHz", row => ((CpuTopologyInfo)row).Mhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 120, SelectionKey: "MHZ"),
            new DisplayTableColumn("Scaling%", row => FormatUsePercent(((CpuTopologyInfo)row).ScalingPercent), DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 130, SelectionKey: "SCALMHZ%"),
            new DisplayTableColumn("MaxMHz", row => ((CpuTopologyInfo)row).MaxMhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 140, SelectionKey: "MAXMHZ"),
            new DisplayTableColumn("MinMHz", row => ((CpuTopologyInfo)row).MinMhz, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 12, Priority: 150, SelectionKey: "MINMHZ"),
            new DisplayTableColumn("BogoMips", row => ((CpuTopologyInfo)row).BogoMips, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 14, Priority: 160, SelectionKey: "BOGOMIPS"),
            new DisplayTableColumn("ModelName", row => ((CpuTopologyInfo)row).ModelName, MinWidth: 8, MaxWidth: 48, Priority: 170, SelectionKey: "MODELNAME"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCpuCacheDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((CpuCacheInfo)row).Name, MinWidth: 3, MaxWidth: 8, Priority: 0, CanHide: false, SelectionKey: "NAME"),
            new DisplayTableColumn("Level", row => ((CpuCacheInfo)row).Level, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 10, SelectionKey: "LEVEL"),
            new DisplayTableColumn("Type", row => ((CpuCacheInfo)row).Type, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "TYPE"),
            new DisplayTableColumn("OneSize", row => ((CpuCacheInfo)row).DisplayOneSize, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 16, Priority: 30, SelectionKey: "ONE-SIZE"),
            new DisplayTableColumn("AllSize", row => ((CpuCacheInfo)row).DisplayAllSize, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 16, Priority: 40, SelectionKey: "ALL-SIZE"),
            new DisplayTableColumn("Ways", row => ((CpuCacheInfo)row).Ways, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 50, SelectionKey: "WAYS"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCpuCacheSelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((CpuCacheInfo)row).Name, MinWidth: 3, MaxWidth: 8, Priority: 0, CanHide: false, SelectionKey: "NAME"),
            new DisplayTableColumn("Level", row => ((CpuCacheInfo)row).Level, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 10, SelectionKey: "LEVEL"),
            new DisplayTableColumn("Type", row => ((CpuCacheInfo)row).Type, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "TYPE"),
            new DisplayTableColumn("OneSize", row => ((CpuCacheInfo)row).DisplayOneSize, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 16, Priority: 30, SelectionKey: "ONE-SIZE"),
            new DisplayTableColumn("AllSize", row => ((CpuCacheInfo)row).DisplayAllSize, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 16, Priority: 40, SelectionKey: "ALL-SIZE"),
            new DisplayTableColumn("Ways", row => ((CpuCacheInfo)row).Ways, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 50, SelectionKey: "WAYS"),
            new DisplayTableColumn("AllocPolicy", row => ((CpuCacheInfo)row).AllocationPolicy, MinWidth: 4, MaxWidth: 18, Priority: 60, SelectionKey: "ALLOC-POLICY"),
            new DisplayTableColumn("WritePolicy", row => ((CpuCacheInfo)row).WritePolicy, MinWidth: 4, MaxWidth: 18, Priority: 70, SelectionKey: "WRITE-POLICY"),
            new DisplayTableColumn("PhyLine", row => ((CpuCacheInfo)row).PhysicalLineCount, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 10, Priority: 80, SelectionKey: "PHY-LINE"),
            new DisplayTableColumn("Sets", row => ((CpuCacheInfo)row).Sets, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 10, Priority: 90, SelectionKey: "SETS"),
            new DisplayTableColumn("CoherencySize", row => ((CpuCacheInfo)row).CoherencySize, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 100, SelectionKey: "COHERENCY-SIZE"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileDescriptorDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Command", row => ((FileDescriptorInfo)row).Command, MinWidth: 6, MaxWidth: 24, Priority: 0, CanHide: false, SelectionKey: "COMMAND"),
            new DisplayTableColumn("Pid", row => ((FileDescriptorInfo)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 10, SelectionKey: "PID"),
            new DisplayTableColumn("User", row => ((FileDescriptorInfo)row).User, MinWidth: 4, MaxWidth: 18, Priority: 20, SelectionKey: "USER"),
            new DisplayTableColumn("Assoc", row => ((FileDescriptorInfo)row).Association, MinWidth: 4, MaxWidth: 10, Priority: 30, SelectionKey: "ASSOC"),
            new DisplayTableColumn("XMode", row => ((FileDescriptorInfo)row).ExtendedMode, MinWidth: 4, MaxWidth: 10, Priority: 40, SelectionKey: "XMODE"),
            new DisplayTableColumn("Type", row => ((FileDescriptorInfo)row).Type, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "TYPE"),
            new DisplayTableColumn("Source", row => ((FileDescriptorInfo)row).Source, MinWidth: 4, MaxWidth: 28, Priority: 60, SelectionKey: "SOURCE"),
            new DisplayTableColumn("MntId", row => ((FileDescriptorInfo)row).MountId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 10, Priority: 70, SelectionKey: "MNTID"),
            new DisplayTableColumn("Inode", row => ((FileDescriptorInfo)row).Inode, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 16, Priority: 80, SelectionKey: "INODE"),
            new DisplayTableColumn("Name", row => ((FileDescriptorInfo)row).Name, MinWidth: 6, MaxWidth: 48, Priority: 90, SelectionKey: "NAME"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileDescriptorSelectableColumns(IReadOnlyList<object> rows)
    {
        var sampleRows = rows.Cast<FileDescriptorInfo>().ToArray();
        var keys = sampleRows
            .SelectMany(row => row.GetAllFieldKeys())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var columns = new List<DisplayTableColumn>(keys.Length);
        var priority = 0;

        foreach (var key in keys)
        {
            var header = BuildReadableColumnHeader(key);
            var alignment = IsNumericFieldKey(key)
                ? DisplayTableAlignment.Right
                : DisplayTableAlignment.Left;

            columns.Add(new DisplayTableColumn(
                header,
                row => ((FileDescriptorInfo)row).GetFieldValue(key),
                alignment,
                MinWidth: 2,
                MaxWidth: key.Contains("NAME", StringComparison.OrdinalIgnoreCase) || key.Contains("PATH", StringComparison.OrdinalIgnoreCase) || key.Contains("SOURCE", StringComparison.OrdinalIgnoreCase) ? 48 : 24,
                Priority: priority,
                SelectionKey: key));
            priority += 10;
        }

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildMountInfoDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Target", row => ((MountInfo)row).Target, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "TARGET", IsTree: true),
            new DisplayTableColumn("Source", row => ((MountInfo)row).Source, MinWidth: 4, MaxWidth: 36, Priority: 10, SelectionKey: "SOURCE"),
            new DisplayTableColumn("FsType", row => ((MountInfo)row).FileSystemType, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "FSTYPE"),
            new DisplayTableColumn("Size", row => ((MountInfo)row).DisplaySize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 30, SelectionKey: "SIZE"),
            new DisplayTableColumn("Use%", row => ((MountInfo)row).UseText, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 40, SelectionKey: "USE%"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildMountInfoSelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Target", row => ((MountInfo)row).Target, MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "TARGET", IsTree: true),
            new DisplayTableColumn("Source", row => ((MountInfo)row).Source, MinWidth: 4, MaxWidth: 36, Priority: 10, SelectionKey: "SOURCE"),
            new DisplayTableColumn("Sources", row => ((MountInfo)row).SourcesText, MinWidth: 4, MaxWidth: 36, Priority: 20, SelectionKey: "SOURCES"),
            new DisplayTableColumn("FsType", row => ((MountInfo)row).FileSystemType, MinWidth: 4, MaxWidth: 12, Priority: 30, SelectionKey: "FSTYPE"),
            new DisplayTableColumn("FsRoot", row => ((MountInfo)row).FileSystemRoot, MinWidth: 3, MaxWidth: 24, Priority: 40, SelectionKey: "FSROOT"),
            new DisplayTableColumn("Size", row => ((MountInfo)row).DisplaySize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "SIZE"),
            new DisplayTableColumn("Used", row => ((MountInfo)row).DisplayUsed, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 60, SelectionKey: "USED"),
            new DisplayTableColumn("Avail", row => ((MountInfo)row).DisplayAvailable, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 70, SelectionKey: "AVAIL"),
            new DisplayTableColumn("Use%", row => ((MountInfo)row).UseText, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 80, SelectionKey: "USE%"),
            new DisplayTableColumn("Options", row => ((MountInfo)row).Options, MinWidth: 4, MaxWidth: 48, Priority: 90, SelectionKey: "OPTIONS"),
            new DisplayTableColumn("FsOptions", row => ((MountInfo)row).FileSystemOptions, MinWidth: 4, MaxWidth: 48, Priority: 100, SelectionKey: "FS-OPTIONS"),
            new DisplayTableColumn("VfsOptions", row => ((MountInfo)row).VfsOptions, MinWidth: 4, MaxWidth: 48, Priority: 110, SelectionKey: "VFS-OPTIONS"),
            new DisplayTableColumn("OptFields", row => ((MountInfo)row).OptionalFields, MinWidth: 4, MaxWidth: 24, Priority: 120, SelectionKey: "OPT-FIELDS"),
            new DisplayTableColumn("Propagation", row => ((MountInfo)row).Propagation, MinWidth: 4, MaxWidth: 12, Priority: 130, SelectionKey: "PROPAGATION"),
            new DisplayTableColumn("Label", row => ((MountInfo)row).Label, MinWidth: 4, MaxWidth: 20, Priority: 140, SelectionKey: "LABEL"),
            new DisplayTableColumn("UUID", row => ((MountInfo)row).Uuid, MinWidth: 8, MaxWidth: 36, Priority: 150, SelectionKey: "UUID"),
            new DisplayTableColumn("PartLabel", row => ((MountInfo)row).PartitionLabel, MinWidth: 4, MaxWidth: 24, Priority: 160, SelectionKey: "PARTLABEL"),
            new DisplayTableColumn("PartUUID", row => ((MountInfo)row).PartitionUuid, MinWidth: 8, MaxWidth: 36, Priority: 170, SelectionKey: "PARTUUID"),
            new DisplayTableColumn("Maj:Min", row => ((MountInfo)row).MajorMinor, MinWidth: 5, MaxWidth: 12, Priority: 180, SelectionKey: "MAJ:MIN"),
            new DisplayTableColumn("Id", row => ((MountInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 8, Priority: 190, SelectionKey: "ID"),
            new DisplayTableColumn("Parent", row => ((MountInfo)row).ParentId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 8, Priority: 200, SelectionKey: "PARENT"),
            new DisplayTableColumn("Tid", row => ((MountInfo)row).TaskId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 10, Priority: 210, SelectionKey: "TID"),
            new DisplayTableColumn("UniqId", row => ((MountInfo)row).UniqueId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 16, Priority: 220, SelectionKey: "UNIQ-ID"),
            new DisplayTableColumn("Freq", row => ((MountInfo)row).FrequencyDays, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 8, Priority: 230, SelectionKey: "FREQ"),
            new DisplayTableColumn("PassNo", row => ((MountInfo)row).PassNumber, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 8, Priority: 240, SelectionKey: "PASSNO"),
            new DisplayTableColumn("InoTotal", row => ((MountInfo)row).InodesTotal, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 250, SelectionKey: "INO.TOTAL"),
            new DisplayTableColumn("InoUsed", row => ((MountInfo)row).InodesUsed, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 260, SelectionKey: "INO.USED"),
            new DisplayTableColumn("InoAvail", row => ((MountInfo)row).InodesAvailable, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 270, SelectionKey: "INO.AVAIL"),
            new DisplayTableColumn("InoUse%", row => ((MountInfo)row).InodeUseText, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 280, SelectionKey: "INO.USE%"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildBlockDeviceSelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((BlockDeviceInfo)row).Name, MinWidth: 3, MaxWidth: 40, Priority: 0, CanHide: false, SelectionKey: "NAME", IsTree: true),
            new DisplayTableColumn("KName", row => ((BlockDeviceInfo)row).KernelName, MinWidth: 3, MaxWidth: 24, Priority: 10, SelectionKey: "KNAME"),
            new DisplayTableColumn("Path", row => ((BlockDeviceInfo)row).Path, MinWidth: 8, MaxWidth: 40, Priority: 20, SelectionKey: "PATH"),
            new DisplayTableColumn("Maj:Min", row => ((BlockDeviceInfo)row).MajorMinor, MinWidth: 5, MaxWidth: 12, Priority: 30, SelectionKey: "MAJ:MIN"),
            new DisplayTableColumn("RM", row => ((BlockDeviceInfo)row).Removable, MinWidth: 2, MaxWidth: 5, Priority: 40, SelectionKey: "RM"),
            new DisplayTableColumn("Size", row => ((BlockDeviceInfo)row).DisplaySize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "SIZE"),
            new DisplayTableColumn("RO", row => ((BlockDeviceInfo)row).ReadOnly, MinWidth: 2, MaxWidth: 5, Priority: 60, SelectionKey: "RO"),
            new DisplayTableColumn("Type", row => ((BlockDeviceInfo)row).Type, MinWidth: 4, MaxWidth: 12, Priority: 70, SelectionKey: "TYPE"),
            new DisplayTableColumn("MountPoint", row => ((BlockDeviceInfo)row).MountPoint, MinWidth: 4, MaxWidth: 32, Priority: 80, SelectionKey: "MOUNTPOINT"),
            new DisplayTableColumn("MountPoints", row => ((BlockDeviceInfo)row).MountPointsText, MinWidth: 4, MaxWidth: 36, Priority: 90, SelectionKey: "MOUNTPOINTS"),
            new DisplayTableColumn("FsType", row => ((BlockDeviceInfo)row).FileSystemType, MinWidth: 4, MaxWidth: 12, Priority: 100, SelectionKey: "FSTYPE"),
            new DisplayTableColumn("FsVer", row => ((BlockDeviceInfo)row).FileSystemVersion, MinWidth: 4, MaxWidth: 12, Priority: 110, SelectionKey: "FSVER"),
            new DisplayTableColumn("Label", row => ((BlockDeviceInfo)row).Label, MinWidth: 4, MaxWidth: 20, Priority: 120, SelectionKey: "LABEL"),
            new DisplayTableColumn("UUID", row => ((BlockDeviceInfo)row).Uuid, MinWidth: 8, MaxWidth: 36, Priority: 130, SelectionKey: "UUID"),
            new DisplayTableColumn("FsAvail", row => ((BlockDeviceInfo)row).DisplayFileSystemAvailable, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 140, SelectionKey: "FSAVAIL"),
            new DisplayTableColumn("FsSize", row => ((BlockDeviceInfo)row).DisplayFileSystemSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 150, SelectionKey: "FSSIZE"),
            new DisplayTableColumn("FsUsed", row => ((BlockDeviceInfo)row).DisplayFileSystemUsed, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 160, SelectionKey: "FSUSED"),
            new DisplayTableColumn("FsUse%", row => ((BlockDeviceInfo)row).FileSystemUseText, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 170, SelectionKey: "FSUSE%"),
            new DisplayTableColumn("FsRoots", row => ((BlockDeviceInfo)row).FileSystemRootsText, MinWidth: 4, MaxWidth: 24, Priority: 180, SelectionKey: "FSROOTS"),
            new DisplayTableColumn("Model", row => ((BlockDeviceInfo)row).Model, MinWidth: 4, MaxWidth: 24, Priority: 190, SelectionKey: "MODEL"),
            new DisplayTableColumn("Serial", row => ((BlockDeviceInfo)row).Serial, MinWidth: 4, MaxWidth: 24, Priority: 200, SelectionKey: "SERIAL"),
            new DisplayTableColumn("Vendor", row => ((BlockDeviceInfo)row).Vendor, MinWidth: 4, MaxWidth: 16, Priority: 210, SelectionKey: "VENDOR"),
            new DisplayTableColumn("Tran", row => ((BlockDeviceInfo)row).Transport, MinWidth: 4, MaxWidth: 12, Priority: 220, SelectionKey: "TRAN"),
            new DisplayTableColumn("State", row => ((BlockDeviceInfo)row).State, MinWidth: 4, MaxWidth: 14, Priority: 230, SelectionKey: "STATE"),
            new DisplayTableColumn("Owner", row => ((BlockDeviceInfo)row).Owner, MinWidth: 4, MaxWidth: 16, Priority: 240, SelectionKey: "OWNER"),
            new DisplayTableColumn("Group", row => ((BlockDeviceInfo)row).Group, MinWidth: 4, MaxWidth: 16, Priority: 250, SelectionKey: "GROUP"),
            new DisplayTableColumn("Mode", row => ((BlockDeviceInfo)row).Mode, MinWidth: 4, MaxWidth: 16, Priority: 260, SelectionKey: "MODE"),
            new DisplayTableColumn("Hctl", row => ((BlockDeviceInfo)row).Hctl, MinWidth: 4, MaxWidth: 12, Priority: 270, SelectionKey: "HCTL"),
            new DisplayTableColumn("Subsys", row => ((BlockDeviceInfo)row).Subsystems, MinWidth: 4, MaxWidth: 24, Priority: 280, SelectionKey: "SUBSYSTEMS"),
            new DisplayTableColumn("Hotplug", row => ((BlockDeviceInfo)row).HotPlug, MinWidth: 4, MaxWidth: 5, Priority: 290, SelectionKey: "HOTPLUG"),
            new DisplayTableColumn("Rota", row => ((BlockDeviceInfo)row).Rotational, MinWidth: 4, MaxWidth: 5, Priority: 300, SelectionKey: "ROTA"),
            new DisplayTableColumn("Rand", row => ((BlockDeviceInfo)row).Random, MinWidth: 4, MaxWidth: 5, Priority: 310, SelectionKey: "RAND"),
            new DisplayTableColumn("Dax", row => ((BlockDeviceInfo)row).Dax, MinWidth: 3, MaxWidth: 5, Priority: 320, SelectionKey: "DAX"),
            new DisplayTableColumn("Alignment", row => ((BlockDeviceInfo)row).Alignment, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 330, SelectionKey: "ALIGNMENT"),
            new DisplayTableColumn("LogSec", row => ((BlockDeviceInfo)row).LogicalSectorSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 340, SelectionKey: "LOG-SEC"),
            new DisplayTableColumn("PhySec", row => ((BlockDeviceInfo)row).PhysicalSectorSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 350, SelectionKey: "PHY-SEC"),
            new DisplayTableColumn("MinIO", row => ((BlockDeviceInfo)row).MinimumIoSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 360, SelectionKey: "MIN-IO"),
            new DisplayTableColumn("OptIO", row => ((BlockDeviceInfo)row).OptimalIoSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 370, SelectionKey: "OPT-IO"),
            new DisplayTableColumn("RQSize", row => ((BlockDeviceInfo)row).RequestQueueSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 380, SelectionKey: "RQ-SIZE"),
            new DisplayTableColumn("RA", row => ((BlockDeviceInfo)row).ReadAhead, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 10, Priority: 390, SelectionKey: "RA"),
            new DisplayTableColumn("Sched", row => ((BlockDeviceInfo)row).Scheduler, MinWidth: 4, MaxWidth: 16, Priority: 400, SelectionKey: "SCHED"),
            new DisplayTableColumn("DiscAln", row => ((BlockDeviceInfo)row).DisplayDiscardAlignment, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 410, SelectionKey: "DISC-ALN"),
            new DisplayTableColumn("DiscGran", row => ((BlockDeviceInfo)row).DisplayDiscardGranularity, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 420, SelectionKey: "DISC-GRAN"),
            new DisplayTableColumn("DiscMax", row => ((BlockDeviceInfo)row).DisplayDiscardMax, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 430, SelectionKey: "DISC-MAX"),
            new DisplayTableColumn("DiscZero", row => ((BlockDeviceInfo)row).DiscardZero, MinWidth: 4, MaxWidth: 5, Priority: 440, SelectionKey: "DISC-ZERO"),
            new DisplayTableColumn("WSame", row => ((BlockDeviceInfo)row).DisplayWSame, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 450, SelectionKey: "WSAME"),
            new DisplayTableColumn("PkName", row => ((BlockDeviceInfo)row).ParentKernelName, MinWidth: 4, MaxWidth: 24, Priority: 460, SelectionKey: "PKNAME"),
            new DisplayTableColumn("PartN", row => ((BlockDeviceInfo)row).PartitionNumber, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 8, Priority: 470, SelectionKey: "PARTN"),
            new DisplayTableColumn("PartLabel", row => ((BlockDeviceInfo)row).PartitionLabel, MinWidth: 4, MaxWidth: 28, Priority: 480, SelectionKey: "PARTLABEL"),
            new DisplayTableColumn("PartUUID", row => ((BlockDeviceInfo)row).PartitionUuid, MinWidth: 8, MaxWidth: 36, Priority: 490, SelectionKey: "PARTUUID"),
            new DisplayTableColumn("PartType", row => ((BlockDeviceInfo)row).PartitionType, MinWidth: 8, MaxWidth: 36, Priority: 500, SelectionKey: "PARTTYPE"),
            new DisplayTableColumn("PartTypeName", row => ((BlockDeviceInfo)row).PartitionTypeName, MinWidth: 8, MaxWidth: 28, Priority: 510, SelectionKey: "PARTTYPENAME"),
            new DisplayTableColumn("PtType", row => ((BlockDeviceInfo)row).PartitionTableType, MinWidth: 4, MaxWidth: 12, Priority: 520, SelectionKey: "PTTYPE"),
            new DisplayTableColumn("PtUUID", row => ((BlockDeviceInfo)row).PartitionTableUuid, MinWidth: 8, MaxWidth: 36, Priority: 530, SelectionKey: "PTUUID"),
            new DisplayTableColumn("Start", row => ((BlockDeviceInfo)row).Start, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 540, SelectionKey: "START"),
            new DisplayTableColumn("DiskSeq", row => ((BlockDeviceInfo)row).DiskSequence, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 550, SelectionKey: "DISK-SEQ"),
            new DisplayTableColumn("Zoned", row => ((BlockDeviceInfo)row).Zoned, MinWidth: 4, MaxWidth: 10, Priority: 560, SelectionKey: "ZONED"),
            new DisplayTableColumn("ZoneSz", row => ((BlockDeviceInfo)row).DisplayZoneSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 570, SelectionKey: "ZONE-SZ"),
            new DisplayTableColumn("ZoneWGran", row => ((BlockDeviceInfo)row).DisplayZoneWriteGranularity, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 580, SelectionKey: "ZONE-WGRAN"),
            new DisplayTableColumn("ZoneApp", row => ((BlockDeviceInfo)row).DisplayZoneAppendSize, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 14, Priority: 590, SelectionKey: "ZONE-APP"),
            new DisplayTableColumn("ZoneNr", row => ((BlockDeviceInfo)row).ZoneCount, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 600, SelectionKey: "ZONE-NR"),
            new DisplayTableColumn("ZoneOMax", row => ((BlockDeviceInfo)row).ZoneOpenMax, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 610, SelectionKey: "ZONE-OMAX"),
            new DisplayTableColumn("ZoneAMax", row => ((BlockDeviceInfo)row).ZoneActiveMax, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 620, SelectionKey: "ZONE-AMAX"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTreeEntryDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((TreeEntryInfo)row).ToString(), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "NAME", IsTree: true),
            new DisplayTableColumn("Type", row => ((TreeEntryInfo)row).Type, MinWidth: 4, MaxWidth: 10, Priority: 10, SelectionKey: "TYPE"),
            new DisplayTableColumn("Size", row => ((TreeEntryInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "SIZE"),
            new DisplayTableColumn("Modified", row => ((TreeEntryInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30, SelectionKey: "MODIFIED"),
            new DisplayTableColumn("Permissions", row => ((TreeEntryInfo)row).Permissions, MinWidth: 9, MaxWidth: 12, Priority: 40, SelectionKey: "PROT"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTreeEntrySelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((TreeEntryInfo)row).ToString(), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "NAME", IsTree: true),
            new DisplayTableColumn("Type", row => ((TreeEntryInfo)row).Type, MinWidth: 4, MaxWidth: 10, Priority: 10, SelectionKey: "TYPE"),
            new DisplayTableColumn("Size", row => ((TreeEntryInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "SIZE"),
            new DisplayTableColumn("Modified", row => ((TreeEntryInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30, SelectionKey: "MODIFIED"),
            new DisplayTableColumn("Permissions", row => ((TreeEntryInfo)row).Permissions, MinWidth: 9, MaxWidth: 12, Priority: 40, SelectionKey: "PROT"),
            new DisplayTableColumn("Mode", row => ((TreeEntryInfo)row).Mode, MinWidth: 4, MaxWidth: 8, Priority: 50, SelectionKey: "MODE"),
            new DisplayTableColumn("User", row => ((TreeEntryInfo)row).User, MinWidth: 4, MaxWidth: 16, Priority: 60, SelectionKey: "USER"),
            new DisplayTableColumn("Group", row => ((TreeEntryInfo)row).Group, MinWidth: 4, MaxWidth: 16, Priority: 70, SelectionKey: "GROUP"),
            new DisplayTableColumn("Inode", row => ((TreeEntryInfo)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 80, SelectionKey: "INODE"),
            new DisplayTableColumn("DeviceId", row => ((TreeEntryInfo)row).DeviceId, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 90, SelectionKey: "DEV"),
            new DisplayTableColumn("NumLinks", row => ((TreeEntryInfo)row).NumLinks, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 100, SelectionKey: "NLINK"),
            new DisplayTableColumn("Target", row => ((TreeEntryInfo)row).LinkTarget, MinWidth: 4, MaxWidth: 36, Priority: 110, SelectionKey: "TARGET"),
            new DisplayTableColumn("Path", row => ((TreeEntryInfo)row).FullPath, MinWidth: 8, MaxWidth: 64, Priority: 120, SelectionKey: "PATH"),
            new DisplayTableColumn("Depth", row => ((TreeEntryInfo)row).Depth, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 130, SelectionKey: "DEPTH"),
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

    private static IReadOnlyList<DisplayTableColumn> BuildUnixFileModeColumns(DisplayPreferences preferences)
    {
        return
        [
            new DisplayTableColumn("Mode", row => FormatPermissions((UnixFileMode)row, preferences.UnixFileMode.Mode), MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("UserRead", row => HasMode((UnixFileMode)row, UnixFileMode.UserRead), MinWidth: 4, MaxWidth: 5, Priority: 10),
            new DisplayTableColumn("UserWrite", row => HasMode((UnixFileMode)row, UnixFileMode.UserWrite), MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("UserExecute", row => HasMode((UnixFileMode)row, UnixFileMode.UserExecute), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("GroupRead", row => HasMode((UnixFileMode)row, UnixFileMode.GroupRead), MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("GroupWrite", row => HasMode((UnixFileMode)row, UnixFileMode.GroupWrite), MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("GroupExecute", row => HasMode((UnixFileMode)row, UnixFileMode.GroupExecute), MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("OtherRead", row => HasMode((UnixFileMode)row, UnixFileMode.OtherRead), MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("OtherWrite", row => HasMode((UnixFileMode)row, UnixFileMode.OtherWrite), MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("OtherExecute", row => HasMode((UnixFileMode)row, UnixFileMode.OtherExecute), MinWidth: 4, MaxWidth: 5, Priority: 90),
            new DisplayTableColumn("SetUser", row => HasMode((UnixFileMode)row, UnixFileMode.SetUser), MinWidth: 4, MaxWidth: 5, Priority: 100),
            new DisplayTableColumn("SetGroup", row => HasMode((UnixFileMode)row, UnixFileMode.SetGroup), MinWidth: 4, MaxWidth: 5, Priority: 110),
            new DisplayTableColumn("StickyBit", row => HasMode((UnixFileMode)row, UnixFileMode.StickyBit), MinWidth: 4, MaxWidth: 5, Priority: 120),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileAttributesColumns(DisplayPreferences preferences)
    {
        return
        [
            new DisplayTableColumn("Mode", row => FormatFileAttributes((FileAttributes)row, preferences.FileAttributes.Mode), MinWidth: 4, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Hex", row => $"0x{((int)(FileAttributes)row):X}", MinWidth: 3, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("ReadOnly", row => HasFileAttribute((FileAttributes)row, FileAttributes.ReadOnly), MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("Hidden", row => HasFileAttribute((FileAttributes)row, FileAttributes.Hidden), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("System", row => HasFileAttribute((FileAttributes)row, FileAttributes.System), MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("Directory", row => HasFileAttribute((FileAttributes)row, FileAttributes.Directory), MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("Archive", row => HasFileAttribute((FileAttributes)row, FileAttributes.Archive), MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Normal", row => HasFileAttribute((FileAttributes)row, FileAttributes.Normal), MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("Temporary", row => HasFileAttribute((FileAttributes)row, FileAttributes.Temporary), MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("ReparsePoint", row => HasFileAttribute((FileAttributes)row, FileAttributes.ReparsePoint), MinWidth: 4, MaxWidth: 5, Priority: 90),
            new DisplayTableColumn("Compressed", row => HasFileAttribute((FileAttributes)row, FileAttributes.Compressed), MinWidth: 4, MaxWidth: 5, Priority: 100),
            new DisplayTableColumn("Encrypted", row => HasFileAttribute((FileAttributes)row, FileAttributes.Encrypted), MinWidth: 4, MaxWidth: 5, Priority: 110),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemPrincipalColumns()
    {
        return
        [
            new DisplayTableColumn("DisplayName", row => ((FileSystemPrincipalInfo)row).DisplayName, MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Id", row => ((FileSystemPrincipalInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("Name", row => ((FileSystemPrincipalInfo)row).Name, MinWidth: 1, MaxWidth: 32, Priority: 20),
        ];
    }

    private static string BuildReadableColumnHeader(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var normalized = key.Replace('.', ' ').Replace(':', ' ').Replace('-', ' ');
        var builder = new System.Text.StringBuilder(normalized.Length);
        var previousWasSpace = true;

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            if (!previousWasSpace &&
                builder.Length > 0 &&
                char.IsUpper(character) &&
                char.IsLetter(builder[^1]) &&
                char.IsLower(builder[^1]))
            {
                builder.Append(' ');
            }

            builder.Append(character);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool IsNumericFieldKey(string key)
    {
        return key.EndsWith("ID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("PORT", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("SIZE", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("LEN", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("POS", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("PID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("TID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("INODE", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("MNTID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("FD", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("VALUE", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("RCID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("LCID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("RPORT", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("LPORT", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("PID", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("TID", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("FD", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("SIZE", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("POS", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("INODE", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("MNTID", StringComparison.OrdinalIgnoreCase);
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

    private static string FormatByteArrayHexPreview(byte[] bytes, int maxBytes = 16)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var slice = bytes.Take(maxBytes).Select(value => value.ToString("X2", CultureInfo.InvariantCulture));
        var text = string.Join(" ", slice);
        return bytes.Length > maxBytes ? $"{text} …" : text;
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

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
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

    private static object? GetKeyValuePairComponent(object row, string propertyName)
    {
        return row.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(row);
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

    private static string FormatTuplePreview(ITuple tuple)
    {
        if (tuple.Length == 0)
        {
            return "()";
        }

        var items = Enumerable.Range(0, tuple.Length)
            .Select(index => FormatDisplaySummaryValue(tuple[index]));
        return $"({string.Join(", ", items)})";
    }

    private static string FormatHashSetPreview(object value)
    {
        if (value is not IEnumerable enumerable)
        {
            return "{::}";
        }

        var items = new List<string>();
        var count = 0;

        foreach (var item in enumerable)
        {
            if (count >= 6)
            {
                items.Add("...");
                break;
            }

            items.Add(FormatDisplaySummaryValue(item));
            count++;
        }

        return items.Count == 0
            ? "{::}"
            : $"{{: {string.Join(", ", items)} :}}";
    }

    private static string FormatDisplaySummaryValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (ObjectFormatter.TryFormatSimple(value, isRoot: false, out var simpleText))
        {
            return simpleText;
        }

        return value switch
        {
            Type type => ReflectionMetadataUtilities.GetDisplayName(type),
            MethodBase methodBase => FormatMethodBaseSummary(methodBase),
            PropertyInfo property => FormatPropertyInfoSummary(property),
            ITuple tuple => FormatTuplePreview(tuple),
            EndPoint endPoint => FormatEndPointValue(endPoint),
            DictionaryEntry entry => FormatDictionaryEntrySummary(entry),
            AssemblyName assemblyName => assemblyName.FullName ?? assemblyName.Name ?? "<unknown>",
            Cookie cookie => FormatCookieSummary(cookie),
            CookieCollection cookies => FormatCookieCollectionSummary(cookies),
            CookieContainer cookieContainer => FormatCookieContainerSummary(cookieContainer),
            NetworkCredential credential => FormatNetworkCredentialSummary(credential),
            PhysicalAddress physicalAddress => FormatPhysicalAddressValue(physicalAddress),
            IPHostEntry hostEntry => FormatIpHostEntrySummary(hostEntry),
            WebHeaderCollection webHeaders => FormatWebHeaderCollectionSummary(webHeaders),
            FileVersionInfo fileVersionInfo => FormatFileVersionInfoSummary(fileVersionInfo),
            NetworkInterface networkInterface => FormatNetworkInterfaceSummary(networkInterface),
            AssemblyLoadContext loadContext => FormatAssemblyLoadContextSummary(loadContext),
            ProcessStartInfo startInfo => FormatProcessStartInfoSummary(startInfo),
            ProcessModule processModule => FormatProcessModuleSummary(processModule),
            FileSystemWatcher watcher => FormatFileSystemWatcherSummary(watcher),
            HttpRequestDefinition requestDefinition => FormatHttpRequestDefinitionSummary(requestDefinition),
            HttpResponseInfo responseInfo => FormatHttpResponseInfoSummary(responseInfo),
            HttpFileServerHandle serverHandle => FormatHttpFileServerHandleSummary(serverHandle),
            HttpRequestMessage request => FormatHttpRequestMessageSummary(request),
            HttpResponseMessage response => FormatHttpResponseMessageSummary(response),
            Assembly assembly => assembly.GetName().FullName ?? assembly.GetName().Name ?? "<unknown>",
            FieldInfo field => FormatFieldInfoSummary(field),
            EventInfo eventInfo => FormatEventInfoSummary(eventInfo),
            DriveInfo drive => FormatDriveInfoSummary(drive),
            Process process => FormatProcessSummary(process),
            ParameterInfo parameter => FormatParameterInfoSummary(parameter),
            HttpHeaders headers => FormatHttpHeaders(headers),
            HttpContent content => FormatHttpContentSummary(content),
            StackFrame frame => FormatStackFrameSummary(frame),
            StackTrace trace => FormatStackTraceSummary(trace),
            _ => value.ToString() ?? ObjectFormatter.GetTypeName(value.GetType()),
        };
    }

    private static string FormatDictionaryEntrySummary(DictionaryEntry entry)
    {
        return $"{FormatDisplaySummaryValue(entry.Key)} => {FormatDisplaySummaryValue(entry.Value)}";
    }

    private static string FormatCookieSummary(Cookie cookie)
    {
        var name = NullIfEmpty(cookie.Name) ?? "<unnamed>";
        var value = NullIfEmpty(cookie.Value) ?? "<empty>";
        var path = NullIfEmpty(cookie.Path);
        var domain = NullIfEmpty(cookie.Domain);

        var location = string.Join(
            " ",
            new[] { domain, path }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        return string.IsNullOrWhiteSpace(location)
            ? $"{name}={value}"
            : $"{name}={value} ({location})";
    }

    private static string FormatCookieCollectionSummary(CookieCollection cookies)
    {
        var count = cookies.Count.ToString(CultureInfo.InvariantCulture);
        var items = FormatCookieCollectionItems(cookies, maxItems: 4);
        return $"{count} cookie{(cookies.Count == 1 ? string.Empty : "s")}: {items}";
    }

    private static string FormatCookieCollectionItems(CookieCollection cookies, int maxItems = 12)
    {
        if (cookies.Count == 0)
        {
            return "<none>";
        }

        var items = cookies
            .Cast<Cookie>()
            .Select(FormatCookieSummary)
            .Take(maxItems + 1)
            .ToList();

        if (items.Count > maxItems)
        {
            items = items.Take(maxItems).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, items);
    }

    private static string FormatCookieContainerSummary(CookieContainer container)
    {
        return $"{container.Count.ToString(CultureInfo.InvariantCulture)} cookies (capacity {container.Capacity.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string FormatNetworkCredentialSummary(NetworkCredential credential)
    {
        var userName = NullIfEmpty(credential.UserName) ?? "<anonymous>";
        var domain = NullIfEmpty(credential.Domain);
        return string.IsNullOrWhiteSpace(domain)
            ? userName
            : $"{domain}\\{userName}";
    }

    private static string FormatPhysicalAddressValue(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();

        if (bytes.Length == 0)
        {
            return "<none>";
        }

        return string.Join("-", bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string FormatIpHostEntrySummary(IPHostEntry hostEntry)
    {
        var hostName = NullIfEmpty(hostEntry.HostName) ?? "<unknown>";
        return $"{hostName} ({hostEntry.AddressList.Length.ToString(CultureInfo.InvariantCulture)} addresses)";
    }

    private static string FormatWebHeaderCollectionSummary(WebHeaderCollection headers)
    {
        var count = headers.Count.ToString(CultureInfo.InvariantCulture);
        var keys = FormatStringCollection(headers.AllKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).Cast<string>());
        return $"{count} headers: {keys}";
    }

    private static string FormatFileVersionInfoSummary(FileVersionInfo versionInfo)
    {
        var name = NullIfEmpty(versionInfo.ProductName) ??
                   NullIfEmpty(versionInfo.FileDescription) ??
                   NullIfEmpty(versionInfo.OriginalFilename) ??
                   Path.GetFileName(NullIfEmpty(versionInfo.FileName)) ??
                   "<unknown>";

        var version = NullIfEmpty(versionInfo.ProductVersion) ??
                      NullIfEmpty(versionInfo.FileVersion);

        return string.IsNullOrWhiteSpace(version)
            ? name
            : $"{name} {version}";
    }

    private static string FormatNetworkInterfaceSummary(NetworkInterface networkInterface)
    {
        var name = NullIfEmpty(networkInterface.Name) ?? "<unnamed>";
        return $"{name} ({networkInterface.NetworkInterfaceType}, {networkInterface.OperationalStatus})";
    }

    private static string FormatAssemblyLoadContextSummary(AssemblyLoadContext loadContext)
    {
        var name = NullIfEmpty(loadContext.Name) ?? "<anonymous>";
        var assemblyCount = GetAssemblyLoadContextAssemblyCount(loadContext);
        return $"{name} ({assemblyCount.ToString(CultureInfo.InvariantCulture)} assemblies)";
    }

    private static int GetAssemblyLoadContextAssemblyCount(AssemblyLoadContext loadContext)
    {
        return loadContext.Assemblies.Count();
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

    private static string FormatProcessStartInfoSummary(ProcessStartInfo startInfo)
    {
        var fileName = NullIfEmpty(startInfo.FileName) ?? "<none>";
        var arguments = NullIfEmpty(startInfo.Arguments);

        return string.IsNullOrWhiteSpace(arguments)
            ? fileName
            : $"{fileName} {arguments}";
    }

    private static string FormatProcessModuleSummary(ProcessModule module)
    {
        var name = NullIfEmpty(module.ModuleName) ?? "<unnamed>";
        var size = StorageSize.FromBytes(module.ModuleMemorySize).ToString();
        return $"{name} ({size})";
    }

    private static string FormatFileSystemWatcherSummary(FileSystemWatcher watcher)
    {
        var path = NullIfEmpty(watcher.Path) ?? "<none>";
        var filter = NullIfEmpty(watcher.Filter) ?? "*.*";
        var state = watcher.EnableRaisingEvents ? "watching" : "idle";
        return $"{path} [{filter}] ({state})";
    }

    private static string FormatDriveInfoSummary(DriveInfo drive)
    {
        if (!drive.IsReady)
        {
            return $"{drive.Name} (not ready)";
        }

        var totalSize = SafeGetDriveSize(drive, static value => value.TotalSize);
        return $"{drive.Name} ({SafeGetDriveValue(drive, static value => value.DriveType)}, {totalSize})";
    }

    private static string FormatProcessSummary(Process process)
    {
        var info = ProcessInfo.From(process);
        return $"{info.Id.ToString(CultureInfo.InvariantCulture)} {info.Name}";
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

    private static string FormatIndexValue(Index value)
    {
        return value.IsFromEnd
            ? $"^{value.Value.ToString(CultureInfo.InvariantCulture)}"
            : value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatRangeValue(Range value)
    {
        return $"{FormatIndexValue(value.Start)}..{FormatIndexValue(value.End)}";
    }

    private static string FormatEndPointValue(EndPoint value)
    {
        return value switch
        {
            IPEndPoint ipEndPoint => $"{ipEndPoint.Address}:{ipEndPoint.Port.ToString(CultureInfo.InvariantCulture)}",
            DnsEndPoint dnsEndPoint => $"{dnsEndPoint.Host}:{dnsEndPoint.Port.ToString(CultureInfo.InvariantCulture)}",
            _ => value.ToString() ?? ObjectFormatter.GetTypeName(value.GetType()),
        };
    }

    private static string? GetEndPointHost(EndPoint value)
    {
        return value switch
        {
            IPEndPoint ipEndPoint => ipEndPoint.Address.ToString(),
            DnsEndPoint dnsEndPoint => dnsEndPoint.Host,
            _ => null,
        };
    }

    private static object? GetEndPointPort(EndPoint value)
    {
        return value switch
        {
            IPEndPoint ipEndPoint => ipEndPoint.Port,
            DnsEndPoint dnsEndPoint => dnsEndPoint.Port,
            _ => null,
        };
    }

    private static object? GetEndPointAddressFamily(EndPoint value)
    {
        return value switch
        {
            IPEndPoint ipEndPoint => ipEndPoint.AddressFamily,
            DnsEndPoint dnsEndPoint => dnsEndPoint.AddressFamily,
            _ => null,
        };
    }

    private static string FormatHttpRequestMessageSummary(HttpRequestMessage request)
    {
        var method = request.Method.Method;
        var target = request.RequestUri?.ToString() ?? "<no-uri>";
        return $"{method} {target}";
    }

    private static string FormatHttpRequestDefinitionSummary(HttpRequestDefinition request)
    {
        return $"{request.Method} {request.RequestUri}";
    }

    private static string FormatHttpResponseMessageSummary(HttpResponseMessage response)
    {
        return FormatHttpResponseStatus(response);
    }

    private static string FormatHttpResponseInfoSummary(HttpResponseInfo response)
    {
        var target = response.FinalUri ?? response.RequestUri;
        return target is null
            ? response.Status
            : $"{response.Status} {response.Method} {target}";
    }

    private static string FormatHttpFileServerHandleSummary(HttpFileServerHandle handle)
    {
        var status = handle.IsOpen ? "open" : "closed";
        var protection = handle.RequiresToken ? " protected" : string.Empty;
        return $"{status}{protection} {handle.ShareUrl} -> {handle.RootPath}";
    }

    private static string FormatHttpResponseStatus(HttpResponseMessage response)
    {
        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;
        return $"{code} {reason}";
    }

    private static string? FormatHttpVersion(Version? version)
    {
        return version is null ? null : version.ToString(2);
    }

    private static string FormatHttpContentSummary(HttpContent content)
    {
        var kind = ObjectFormatter.GetTypeName(content.GetType());
        var contentType = content.Headers.ContentType?.ToString();
        var length = content.Headers.ContentLength;

        if (!string.IsNullOrWhiteSpace(contentType) && length is long contentLength)
        {
            return $"{kind} ({contentType}, {contentLength.ToString(CultureInfo.InvariantCulture)} bytes)";
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            return $"{kind} ({contentType})";
        }

        if (length is long byteLength)
        {
            return $"{kind} ({byteLength.ToString(CultureInfo.InvariantCulture)} bytes)";
        }

        return kind;
    }

    private static string FormatProductInfoHeader(IEnumerable<ProductInfoHeaderValue> values)
    {
        return FormatCollectionHeader(values);
    }

    private static string FormatCollectionHeader<T>(IEnumerable<T> values)
    {
        if (values is null)
        {
            return "<none>";
        }

        var items = values
            .Select(value => value?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(8 + 1)
            .ToList();

        if (items.Count == 0)
        {
            return "<none>";
        }

        if (items.Count > 8)
        {
            items = items.Take(8).Append("…").ToList();
        }

        return string.Join(", ", items);
    }

    private static string FormatStringCollection(IEnumerable<string> values)
    {
        return FormatCollectionHeader(values);
    }

    private static string FormatIpAddressCollection(IEnumerable<IPAddress> addresses)
    {
        var items = addresses
            .Select(address => address.ToString())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Take(12 + 1)
            .ToList();

        if (items.Count == 0)
        {
            return "<none>";
        }

        if (items.Count > 12)
        {
            items = items.Take(12).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, items);
    }

    private static string FormatWebHeaderCollectionEntries(WebHeaderCollection headers, int maxEntries = 8)
    {
        var entries = headers.AllKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Take(maxEntries + 1)
            .Select(key => $"{key}: {headers[key]}")
            .ToList();

        if (entries.Count == 0)
        {
            return "<none>";
        }

        if (entries.Count > maxEntries)
        {
            entries = entries.Take(maxEntries).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, entries);
    }

    private static string FormatNetworkSpeed(long speedBitsPerSecond)
    {
        if (speedBitsPerSecond <= 0)
        {
            return "<unknown>";
        }

        var units = new[] { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        double value = speedBitsPerSecond;
        var unitIndex = 0;

        while (value >= 1000d && unitIndex < units.Length - 1)
        {
            value /= 1000d;
            unitIndex++;
        }

        var format = value >= 100d || Math.Abs(value % 1d) < double.Epsilon ? "0" : "0.#";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static bool SafeSupportsComponent(NetworkInterface networkInterface, NetworkInterfaceComponent component)
    {
        try
        {
            return networkInterface.Supports(component);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatNetworkInterfaceAddresses(NetworkInterface networkInterface)
    {
        try
        {
            return FormatIpAddressCollection(networkInterface.GetIPProperties().UnicastAddresses.Select(address => address.Address));
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string FormatNetworkInterfaceGateways(NetworkInterface networkInterface)
    {
        try
        {
            return FormatIpAddressCollection(networkInterface.GetIPProperties().GatewayAddresses.Select(address => address.Address));
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string FormatNetworkInterfaceDnsServers(NetworkInterface networkInterface)
    {
        try
        {
            return FormatIpAddressCollection(networkInterface.GetIPProperties().DnsAddresses);
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static bool SafeHasSecurePassword(NetworkCredential credential)
    {
        try
        {
            return credential.SecurePassword is not null && credential.SecurePassword.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeGetProcessModuleFileVersion(ProcessModule module)
    {
        try
        {
            return NullIfEmpty(module.FileVersionInfo?.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatPointer(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        return $"0x{value.ToInt64().ToString("x", CultureInfo.InvariantCulture)}";
    }

    private static string FormatHttpHeaders(HttpHeaders? headers, int maxEntries = 8)
    {
        if (headers is null)
        {
            return "<none>";
        }

        var entries = headers
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}")
            .Take(maxEntries + 1)
            .ToList();

        if (entries.Count == 0)
        {
            return "<none>";
        }

        if (entries.Count > maxEntries)
        {
            entries = entries.Take(maxEntries).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, entries);
    }

    private static string FormatHttpHeaderDictionary(IReadOnlyDictionary<string, IReadOnlyList<string>>? headers, int maxEntries = 8)
    {
        if (headers is null || headers.Count == 0)
        {
            return "<none>";
        }

        var entries = headers
            .Select(header => $"{header.Key}: {string.Join(", ", header.Value)}")
            .Take(maxEntries + 1)
            .ToList();

        if (entries.Count > maxEntries)
        {
            entries = entries.Take(maxEntries).Append("…").ToList();
        }

        return string.Join(Environment.NewLine, entries);
    }

    private static string FormatFieldInfoSummary(FieldInfo field)
    {
        var modifiers = new List<string>(3);

        if (field.IsStatic)
        {
            modifiers.Add("static");
        }

        if (field.IsInitOnly)
        {
            modifiers.Add("readonly");
        }

        if (field.IsLiteral)
        {
            modifiers.Add("const");
        }

        var prefix = modifiers.Count == 0 ? string.Empty : string.Join(' ', modifiers) + " ";
        return $"{prefix}{GetReadableTypeName(field.FieldType)} {field.Name}";
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

    private static string FormatEventInfoSummary(EventInfo eventInfo)
    {
        return $"{GetReadableTypeName(eventInfo.EventHandlerType)} {eventInfo.Name}";
    }

    private static object? FormatParameterDefaultValue(ParameterInfo parameter)
    {
        return parameter.HasDefaultValue ? parameter.DefaultValue : null;
    }

    private static string FormatParameterInfoSummary(ParameterInfo parameter)
    {
        var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
        return $"{prefix}{GetReadableTypeName(UnwrapByRef(parameter.ParameterType))} {parameter.Name}";
    }

    private static string? GetReadableTypeName(Type? type)
    {
        return type is null ? null : ReflectionMetadataUtilities.GetDisplayName(type);
    }

    private static Type UnwrapByRef(Type type)
    {
        return type.IsByRef ? type.GetElementType() ?? type : type;
    }

    private static string? GetMethodBaseReturnType(MethodBase methodBase)
    {
        return methodBase is MethodInfo methodInfo
            ? GetReadableTypeName(methodInfo.ReturnType)
            : null;
    }

    private static string FormatMethodBaseSummary(MethodBase methodBase)
    {
        return methodBase switch
        {
            MethodInfo methodInfo => ReflectionMetadataUtilities.FormatMethodSignature(methodInfo),
            ConstructorInfo constructorInfo => ReflectionMetadataUtilities.FormatConstructorSignature(constructorInfo),
            _ => methodBase.Name,
        };
    }

    private static bool IsPropertyStatic(PropertyInfo property)
    {
        return (property.GetMethod ?? property.SetMethod)?.IsStatic ?? false;
    }

    private static string FormatPropertyInfoSummary(PropertyInfo property)
    {
        var accessors = new List<string>(2);

        if (property.CanRead)
        {
            accessors.Add("get;");
        }

        if (property.CanWrite)
        {
            accessors.Add("set;");
        }

        var accessorBlock = accessors.Count == 0
            ? "{ }"
            : $"{{ {string.Join(' ', accessors)} }}";
        return $"{GetReadableTypeName(property.PropertyType)} {property.Name} {accessorBlock}";
    }

    private static string? GetStackFrameMethodName(StackFrame frame)
    {
        return frame.GetMethod() is { } method ? FormatMethodBaseSummary(method) : null;
    }

    private static string? GetStackFrameDeclaringType(StackFrame frame)
    {
        return GetReadableTypeName(frame.GetMethod()?.DeclaringType);
    }

    private static string FormatStackFrameSummary(StackFrame frame)
    {
        var method = frame.GetMethod();
        var methodText = method is null ? "<unknown>" : FormatMethodBaseSummary(method);
        var file = NullIfEmpty(frame.GetFileName());
        var line = frame.GetFileLineNumber();

        if (!string.IsNullOrWhiteSpace(file) && line > 0)
        {
            return $"{methodText} at {file}:{line.ToString(CultureInfo.InvariantCulture)}";
        }

        return methodText;
    }

    private static int GetStackFrameCount(StackTrace trace)
    {
        return trace.FrameCount;
    }

    private static string FormatStackTraceSummary(StackTrace trace)
    {
        var count = GetStackFrameCount(trace);
        return $"{count.ToString(CultureInfo.InvariantCulture)} frame{(count == 1 ? string.Empty : "s")}";
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
            TemporalDisplayMode.Local => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Relative => FormatRelativeTime(ToDisplayInstant(value), nowProvider()),
            TemporalDisplayMode.Unix => ToDisplayInstant(value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            TemporalDisplayMode.Custom => value.ToString(format ?? "O", CultureInfo.InvariantCulture),
            _ => value.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatDateTimeOffset(
        DateTimeOffset value,
        TemporalDisplayMode mode,
        string? format,
        Func<DateTimeOffset> nowProvider)
    {
        return mode switch
        {
            TemporalDisplayMode.Iso => value.ToString("O", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Local => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Relative => FormatRelativeTime(value, nowProvider()),
            TemporalDisplayMode.Unix => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
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

    private static string FormatRelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = value - now;
        var isFuture = delta >= TimeSpan.Zero;
        var elapsed = isFuture ? delta : -delta;

        if (elapsed < TimeSpan.FromSeconds(45))
        {
            return isFuture ? "in a few seconds" : "just now";
        }

        if (elapsed < TimeSpan.FromMinutes(90))
        {
            var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(minutes, "minute", isFuture);
        }

        if (elapsed < TimeSpan.FromHours(36))
        {
            var hours = Math.Max(1, (int)Math.Round(elapsed.TotalHours, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(hours, "hour", isFuture);
        }

        if (elapsed < TimeSpan.FromDays(30))
        {
            var days = Math.Max(1, (int)Math.Round(elapsed.TotalDays, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(days, "day", isFuture);
        }

        if (elapsed < TimeSpan.FromDays(365))
        {
            var months = Math.Max(1, (int)Math.Round(elapsed.TotalDays / 30d, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(months, "month", isFuture);
        }

        var years = Math.Max(1, (int)Math.Round(elapsed.TotalDays / 365d, MidpointRounding.AwayFromZero));
        return FormatRelativeUnit(years, "year", isFuture);
    }

    private static string FormatRelativeUnit(int value, string unit, bool isFuture)
    {
        var suffix = value == 1 ? unit : $"{unit}s";
        return isFuture ? $"in {value} {suffix}" : $"{value} {suffix} ago";
    }

    private static string FormatTimeSpan(TimeSpan value, DurationDisplayMode mode, string? format)
    {
        return mode switch
        {
            DurationDisplayMode.Raw => value.ToString("c", CultureInfo.InvariantCulture),
            DurationDisplayMode.Short => FormatShortDuration(value),
            DurationDisplayMode.Long => FormatLongDuration(value),
            DurationDisplayMode.TotalSeconds => FormatTotalSeconds(value),
            DurationDisplayMode.Custom => FormatCustomDuration(value, format),
            _ => value.ToString("c", CultureInfo.InvariantCulture),
        };
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

    private static string FormatShortDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "0s";
        }

        var negative = duration < TimeSpan.Zero;
        var remaining = duration.Duration();
        var parts = new List<string>();

        if (remaining.Days > 0)
        {
            parts.Add($"{remaining.Days}d");
        }

        if (remaining.Hours > 0)
        {
            parts.Add($"{remaining.Hours}h");
        }

        if (remaining.Minutes > 0)
        {
            parts.Add($"{remaining.Minutes}m");
        }

        if (remaining.Seconds > 0)
        {
            parts.Add($"{remaining.Seconds}s");
        }

        if (parts.Count == 0)
        {
            if (remaining.Milliseconds > 0)
            {
                parts.Add($"{remaining.Milliseconds}ms");
            }
            else
            {
                parts.Add("0s");
            }
        }

        return negative ? "-" + string.Join(" ", parts) : string.Join(" ", parts);
    }

    private static string FormatLongDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "0 seconds";
        }

        var negative = duration < TimeSpan.Zero;
        var remaining = duration.Duration();
        var parts = new List<string>();

        AppendLongDurationUnit(parts, remaining.Days, "day");
        AppendLongDurationUnit(parts, remaining.Hours, "hour");
        AppendLongDurationUnit(parts, remaining.Minutes, "minute");
        AppendLongDurationUnit(parts, remaining.Seconds, "second");

        if (parts.Count == 0)
        {
            if (remaining.Milliseconds > 0)
            {
                AppendLongDurationUnit(parts, remaining.Milliseconds, "millisecond");
            }
            else
            {
                parts.Add("0 seconds");
            }
        }

        var text = string.Join(", ", parts);
        return negative ? "-" + text : text;
    }

    private static void AppendLongDurationUnit(List<string> parts, int value, string unit)
    {
        if (value == 0)
        {
            return;
        }

        parts.Add(value == 1 ? $"1 {unit}" : $"{value} {unit}s");
    }

    private static string FormatTotalSeconds(TimeSpan value)
    {
        var seconds = value.TotalSeconds;
        var wholeSeconds = Math.Round(seconds, MidpointRounding.AwayFromZero);

        if (Math.Abs(seconds - wholeSeconds) < 0.0000001d)
        {
            return wholeSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatCustomDuration(TimeSpan value, string? format)
    {
        try
        {
            return value.ToString(string.IsNullOrWhiteSpace(format) ? "c" : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return value.ToString("c", CultureInfo.InvariantCulture);
        }
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

    private static string FormatStorageSize(StorageSize size, StorageSizeDisplayMode mode)
    {
        if (mode == StorageSizeDisplayMode.Bytes)
        {
            return $"{size.Bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var absoluteBytes = Math.Abs((decimal)size.Bytes);
        var sign = size.Bytes < 0 ? "-" : string.Empty;
        var units = new[] { "B", "kB", "MB", "GB", "TB", "PB" };
        var unitIndex = 0;
        var scaled = absoluteBytes;

        while (scaled >= 1000m && unitIndex < units.Length - 1)
        {
            scaled /= 1000m;
            unitIndex++;
        }

        var format = unitIndex == 0 || scaled >= 100m ? "0" : "0.#";
        return $"{sign}{scaled.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static string FormatUsePercent(int? percent)
    {
        return percent is null
            ? string.Empty
            : $"{percent.Value.ToString(CultureInfo.InvariantCulture)}%";
    }

    private static string FormatPingDuration(TimeSpan? duration)
    {
        return duration is null
            ? string.Empty
            : $"{duration.Value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";
    }

    private static FileSystemEntry GetDisplayEntry(FileSystemInfo value)
    {
        return FileSystemEntry.From(value, preferLongDisplay: false);
    }

    private static object? SafeGetDriveValue<T>(DriveInfo drive, Func<DriveInfo, T> getValue)
    {
        try
        {
            return getValue(drive);
        }
        catch
        {
            return null;
        }
    }

    private static StorageSize? SafeGetDriveSize(DriveInfo drive, Func<DriveInfo, long> getValue)
    {
        try
        {
            return StorageSize.FromBytes(getValue(drive));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPermissions(UnixFileMode mode, UnixFileModeDisplayMode displayMode)
    {
        Span<char> characters = stackalloc char[9];
        characters[0] = HasMode(mode, UnixFileMode.UserRead) ? 'r' : '-';
        characters[1] = HasMode(mode, UnixFileMode.UserWrite) ? 'w' : '-';
        characters[2] = GetExecuteCharacter(mode, UnixFileMode.UserExecute, UnixFileMode.SetUser, 's', 'S');
        characters[3] = HasMode(mode, UnixFileMode.GroupRead) ? 'r' : '-';
        characters[4] = HasMode(mode, UnixFileMode.GroupWrite) ? 'w' : '-';
        characters[5] = GetExecuteCharacter(mode, UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's', 'S');
        characters[6] = HasMode(mode, UnixFileMode.OtherRead) ? 'r' : '-';
        characters[7] = HasMode(mode, UnixFileMode.OtherWrite) ? 'w' : '-';
        characters[8] = GetExecuteCharacter(mode, UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't', 'T');
        var symbolic = new string(characters);
        var octal = $"0{Convert.ToString((int)mode, 8)}";

        return displayMode switch
        {
            UnixFileModeDisplayMode.Octal => octal,
            UnixFileModeDisplayMode.Both => $"{symbolic} ({octal})",
            _ => symbolic,
        };
    }

    private static string FormatFileAttributes(FileAttributes attributes, FileAttributesDisplayMode displayMode)
    {
        var names = attributes.ToString();
        var hex = $"0x{((int)attributes):X}";

        return displayMode switch
        {
            FileAttributesDisplayMode.Hex => hex,
            FileAttributesDisplayMode.Both => $"{names} ({hex})",
            _ => names,
        };
    }

    private static bool HasFileAttribute(FileAttributes attributes, FileAttributes flag)
    {
        return (attributes & flag) == flag;
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

    private static string FormatColorName(Color color)
    {
        if (color.IsEmpty)
        {
            return "<empty>";
        }

        if (color.IsNamedColor || color.IsKnownColor)
        {
            return color.Name;
        }

        return FormatColorHex(color);
    }

    private static string FormatColorHex(Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static IReadOnlyList<DisplayTableColumn> BuildRecordColumns(IReadOnlyList<object> rows)
    {
        var fieldNames = rows
            .SelectMany(row => ShellRecordUtilities.TryGetVisibleFields(row, out var fields)
                ? fields.Select(field => field.Key)
                : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return fieldNames
            .Select((fieldName, index) => new DisplayTableColumn(
                fieldName,
                row => ShellRecordUtilities.TryGetValue(row, fieldName, out var value) ? value : null,
                Alignment: ShouldRightAlignRecordField(rows, fieldName) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left,
                Priority: index,
                CanHide: index > 0))
            .ToArray();
    }

    private static bool ShouldRightAlignRecordField(IReadOnlyList<object> rows, string fieldName)
    {
        foreach (var row in rows)
        {
            if (!ShellRecordUtilities.TryGetValue(row, fieldName, out var value) || value is null)
            {
                continue;
            }

            var effectiveType = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();

            return effectiveType == typeof(byte) ||
                   effectiveType == typeof(short) ||
                   effectiveType == typeof(int) ||
                   effectiveType == typeof(long) ||
                   effectiveType == typeof(StorageSize) ||
                   effectiveType == typeof(float) ||
                   effectiveType == typeof(double) ||
                   effectiveType == typeof(decimal);
        }

        return false;
    }

    private static bool HasMode(UnixFileMode value, UnixFileMode flag) => (value & flag) == flag;

    private static char GetExecuteCharacter(
        UnixFileMode mode,
        UnixFileMode executeFlag,
        UnixFileMode specialFlag,
        char specialWhenExecute,
        char specialWhenNotExecute)
    {
        var hasExecute = HasMode(mode, executeFlag);
        var hasSpecial = HasMode(mode, specialFlag);

        if (hasSpecial)
        {
            return hasExecute ? specialWhenExecute : specialWhenNotExecute;
        }

        return hasExecute ? 'x' : '-';
    }

    private static DisplayProfile CreateStyledTextProfile()
    {
        return DisplayProfile
            .For<StyledText>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((StyledText)context.Value).ToAnsi());
    }

    private static DisplayProfile CreateCommandResultProfile()
    {
        return DisplayProfile
            .For<CommandSuccess>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((CommandSuccess)context.Value).Message);
    }

    private static DisplayProfile CreateEventRaiseResultProfile()
    {
        return DisplayProfile
            .For<EventRaiseResult>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ((EventRaiseResult)context.Value).ToString())
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Event", row => ((EventRaiseResult)row).EventName, MinWidth: 10, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Handled", row => ((EventRaiseResult)row).Handled ? "yes" : "no", MinWidth: 7, MaxWidth: 7, Priority: 10),
                    new DisplayTableColumn("Handlers", row => ((EventRaiseResult)row).HandlersInvoked, DisplayTableAlignment.Right, MinWidth: 8, MaxWidth: 10, Priority: 20),
                    new DisplayTableColumn("Cancelled", row => ((EventRaiseResult)row).Cancelled ? "yes" : "no", MinWidth: 9, MaxWidth: 9, Priority: 30),
                ]);
    }

    private static DisplayProfile CreateShellEventHandlerProfile()
    {
        return DisplayProfile
            .For<ShellEventHandler>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ((ShellEventHandler)context.Value).ToString())
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Event", row => ((ShellEventHandler)row).EventName, MinWidth: 10, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Handler", row => ((ShellEventHandler)row).HandlerName, MinWidth: 10, MaxWidth: 32, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Priority", row => ((ShellEventHandler)row).Priority?.ToString(CultureInfo.InvariantCulture) ?? "—", DisplayTableAlignment.Right, MinWidth: 8, MaxWidth: 10, Priority: 20),
                    new DisplayTableColumn("Once", row => ((ShellEventHandler)row).Once ? "yes" : "no", MinWidth: 4, MaxWidth: 5, Priority: 30),
                ]);
    }

    private static DisplayProfile CreateEventHandlerRemovalResultProfile()
    {
        return DisplayProfile
            .For<Shell.EventHandlerRemovalResult>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var result = (Shell.EventHandlerRemovalResult)context.Value;
                    return $"Removed handler '{result.HandlerName}' from event '{result.EventName}'.";
                });
    }

    private static DisplayProfile CreateEventClearResultProfile()
    {
        return DisplayProfile
            .For<Shell.EventClearResult>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var result = (Shell.EventClearResult)context.Value;
                    return $"Cleared {result.HandlersRemoved} handler(s) from event '{result.EventName}'.";
                });
    }

    private static DisplayProfile CreateRemovedEntryProfile()
    {
        return DisplayProfile
            .For<RemovedEntry>()
            .AddTableCase(_ =>
            [
                new DisplayTableColumn("Name", row => ((RemovedEntry)row).ToString(), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, IsTree: true),
                new DisplayTableColumn("Type", row => ((RemovedEntry)row).IsDirectory ? "dir" : "file", MinWidth: 4, MaxWidth: 10, Priority: 5),
                new DisplayTableColumn("Size", row => ((RemovedEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 10),
            ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var entry = (RemovedEntry)context.Value;
                    return entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
                });
    }

    // ── Streams and I/O ──────────────────────────────────────────────────

    private static DisplayProfile CreateStreamProfile()
    {
        return DisplayProfile
            .For<Stream>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildStreamColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var stream = (Stream)context.Value;
                    var type = stream.GetType().Name;
                    var len = stream.CanSeek ? $"{stream.Length} bytes" : "non-seekable";
                    var flags = string.Join("/",
                        new[] { stream.CanRead ? "R" : null, stream.CanWrite ? "W" : null, stream.CanSeek ? "S" : null }
                        .Where(f => f is not null));
                    return $"{type} ({len}, {flags})";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildStreamColumns()
    {
        return
        [
            new DisplayTableColumn("Type", row => row.GetType().Name, MinWidth: 6, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Length", row => ((Stream)row).CanSeek ? ((Stream)row).Length : null, DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 16, Priority: 10),
            new DisplayTableColumn("Position", row => ((Stream)row).CanSeek ? ((Stream)row).Position : null, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("CanRead", row => ((Stream)row).CanRead, MinWidth: 5, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("CanWrite", row => ((Stream)row).CanWrite, MinWidth: 5, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("CanSeek", row => ((Stream)row).CanSeek, MinWidth: 5, MaxWidth: 5, Priority: 50),
        ];
    }

    private static DisplayProfile CreateStreamReaderProfile()
    {
        return DisplayProfile
            .For<StreamReader>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Encoding", row => ((StreamReader)row).CurrentEncoding.WebName, MinWidth: 5, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("EndOfStream", row => ((StreamReader)row).EndOfStream, MinWidth: 5, MaxWidth: 5, Priority: 10),
                    new DisplayTableColumn("BaseStream", row => ((StreamReader)row).BaseStream.GetType().Name, MinWidth: 6, MaxWidth: 24, Priority: 20),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var reader = (StreamReader)context.Value;
                    return $"StreamReader ({reader.CurrentEncoding.WebName}, eof={reader.EndOfStream})";
                });
    }

    private static DisplayProfile CreateStreamWriterProfile()
    {
        return DisplayProfile
            .For<StreamWriter>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Encoding", row => ((StreamWriter)row).Encoding.WebName, MinWidth: 5, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("AutoFlush", row => ((StreamWriter)row).AutoFlush, MinWidth: 5, MaxWidth: 5, Priority: 10),
                    new DisplayTableColumn("BaseStream", row => ((StreamWriter)row).BaseStream.GetType().Name, MinWidth: 6, MaxWidth: 24, Priority: 20),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var writer = (StreamWriter)context.Value;
                    return $"StreamWriter ({writer.Encoding.WebName}, autoflush={writer.AutoFlush})";
                });
    }

    private static DisplayProfile CreateZipArchiveProfile()
    {
        return DisplayProfile
            .For<ZipArchive>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Mode", row => ((ZipArchive)row).Mode.ToString(), MinWidth: 4, MaxWidth: 10, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Entries", row => ((ZipArchive)row).Entries.Count, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var zip = (ZipArchive)context.Value;
                    return $"ZipArchive ({zip.Mode}, {zip.Entries.Count} entries)";
                });
    }

    private static DisplayProfile CreateZipArchiveEntryProfile()
    {
        return DisplayProfile
            .For<ZipArchiveEntry>()
            .AddTableCase(_ =>
            [
                new DisplayTableColumn("Name", row => ((ZipArchiveEntry)row).FullName, MinWidth: 8, MaxWidth: 64, Priority: 0, CanHide: false),
                new DisplayTableColumn("Size", row => new StorageSize(((ZipArchiveEntry)row).Length), DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 12, Priority: 10),
                new DisplayTableColumn("Compressed", row => new StorageSize(((ZipArchiveEntry)row).CompressedLength), DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 12, Priority: 20),
                new DisplayTableColumn("Modified", row => ((ZipArchiveEntry)row).LastWriteTime, MinWidth: 10, MaxWidth: 20, Priority: 30),
            ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var entry = (ZipArchiveEntry)context.Value;
                    return $"{entry.FullName} ({new StorageSize(entry.Length)})";
                });
    }

    // ── Platform and Runtime ─────────────────────────────────────────────

    private static DisplayProfile CreateOperatingSystemProfile()
    {
        return DisplayProfile
            .For<OperatingSystem>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Platform", row => ((OperatingSystem)row).Platform.ToString(), MinWidth: 5, MaxWidth: 12, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Version", row => ((OperatingSystem)row).Version.ToString(), MinWidth: 5, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("ServicePack", row => NullIfEmpty(((OperatingSystem)row).ServicePack), MinWidth: 3, MaxWidth: 24, Priority: 20),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((OperatingSystem)context.Value).ToString());
    }

    private static DisplayProfile CreateArchitectureProfile()
    {
        return DisplayProfile
            .For<Architecture>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((Architecture)context.Value).ToString().ToLowerInvariant());
    }

    private static DisplayProfile CreateRuntimeInformationProfile()
    {
        // RuntimeInformation is a static class — create a snapshot record for display
        return DisplayProfile
            .For<RuntimeInformationSnapshot>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("OS", row => ((RuntimeInformationSnapshot)row).OSDescription, MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Arch", row => ((RuntimeInformationSnapshot)row).OSArchitecture.ToString().ToLowerInvariant(), MinWidth: 4, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Framework", row => ((RuntimeInformationSnapshot)row).FrameworkDescription, MinWidth: 8, MaxWidth: 32, Priority: 20),
                    new DisplayTableColumn("Runtime", row => ((RuntimeInformationSnapshot)row).RuntimeIdentifier, MinWidth: 6, MaxWidth: 20, Priority: 30),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var info = (RuntimeInformationSnapshot)context.Value;
                    return $"{info.FrameworkDescription} ({info.OSArchitecture.ToString().ToLowerInvariant()})";
                });
    }

    // ── Security and Identity ────────────────────────────────────────────

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

    private static DisplayProfile CreateBigIntegerProfile()
    {
        return DisplayProfile
            .For<BigInteger>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((BigInteger)context.Value).ToString(CultureInfo.InvariantCulture));
    }

    private static DisplayProfile CreateComplexProfile()
    {
        return DisplayProfile
            .For<Complex>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Real", row => ((Complex)row).Real.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Imaginary", row => ((Complex)row).Imaginary.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 20, Priority: 10),
                    new DisplayTableColumn("Magnitude", row => ((Complex)row).Magnitude.ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 20),
                    new DisplayTableColumn("Phase", row => ((Complex)row).Phase.ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 30),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var c = (Complex)context.Value;
                    var sign = c.Imaginary >= 0 ? "+" : "-";
                    return $"{c.Real.ToString(CultureInfo.InvariantCulture)} {sign} {Math.Abs(c.Imaginary).ToString(CultureInfo.InvariantCulture)}i";
                });
    }

    private static DisplayProfile CreateVector2Profile()
    {
        return DisplayProfile
            .For<Vector2>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("X", row => ((Vector2)row).X.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Y", row => ((Vector2)row).Y.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Length", row => ((Vector2)row).Length().ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 10),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var v = (Vector2)context.Value;
                    return $"({v.X.ToString(CultureInfo.InvariantCulture)}, {v.Y.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateVector3Profile()
    {
        return DisplayProfile
            .For<Vector3>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("X", row => ((Vector3)row).X.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Y", row => ((Vector3)row).Y.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Z", row => ((Vector3)row).Z.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Length", row => ((Vector3)row).Length().ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 10),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var v = (Vector3)context.Value;
                    return $"({v.X.ToString(CultureInfo.InvariantCulture)}, {v.Y.ToString(CultureInfo.InvariantCulture)}, {v.Z.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateVector4Profile()
    {
        return DisplayProfile
            .For<Vector4>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("X", row => ((Vector4)row).X.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Y", row => ((Vector4)row).Y.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Z", row => ((Vector4)row).Z.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("W", row => ((Vector4)row).W.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Length", row => ((Vector4)row).Length().ToString("G6", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 10),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var v = (Vector4)context.Value;
                    return $"({v.X.ToString(CultureInfo.InvariantCulture)}, {v.Y.ToString(CultureInfo.InvariantCulture)}, {v.Z.ToString(CultureInfo.InvariantCulture)}, {v.W.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateQuaternionProfile()
    {
        return DisplayProfile
            .For<Quaternion>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("W", row => ((Quaternion)row).W.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("X", row => ((Quaternion)row).X.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Y", row => ((Quaternion)row).Y.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Z", row => ((Quaternion)row).Z.ToString(CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("IsIdentity", row => ((Quaternion)row).IsIdentity ? "Yes" : "No", DisplayTableAlignment.Left, MinWidth: 3, MaxWidth: 10, Priority: 10),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var q = (Quaternion)context.Value;
                    return $"({q.W.ToString(CultureInfo.InvariantCulture)}; {q.X.ToString(CultureInfo.InvariantCulture)}, {q.Y.ToString(CultureInfo.InvariantCulture)}, {q.Z.ToString(CultureInfo.InvariantCulture)})";
                });
    }

    private static DisplayProfile CreateMatrix4x4Profile()
    {
        return DisplayProfile
            .For<Matrix4x4>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("M11", row => ((Matrix4x4)row).M11.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 0, CanHide: false),
                    new DisplayTableColumn("M12", row => ((Matrix4x4)row).M12.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 1, CanHide: false),
                    new DisplayTableColumn("M13", row => ((Matrix4x4)row).M13.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 2, CanHide: false),
                    new DisplayTableColumn("M14", row => ((Matrix4x4)row).M14.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 3, CanHide: false),
                    new DisplayTableColumn("M21", row => ((Matrix4x4)row).M21.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 4),
                    new DisplayTableColumn("M22", row => ((Matrix4x4)row).M22.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 5),
                    new DisplayTableColumn("M23", row => ((Matrix4x4)row).M23.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 6),
                    new DisplayTableColumn("M24", row => ((Matrix4x4)row).M24.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 7),
                    new DisplayTableColumn("M31", row => ((Matrix4x4)row).M31.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 8),
                    new DisplayTableColumn("M32", row => ((Matrix4x4)row).M32.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 9),
                    new DisplayTableColumn("M33", row => ((Matrix4x4)row).M33.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("M34", row => ((Matrix4x4)row).M34.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 11),
                    new DisplayTableColumn("M41", row => ((Matrix4x4)row).M41.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 12),
                    new DisplayTableColumn("M42", row => ((Matrix4x4)row).M42.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 13),
                    new DisplayTableColumn("M43", row => ((Matrix4x4)row).M43.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 14),
                    new DisplayTableColumn("M44", row => ((Matrix4x4)row).M44.ToString("G4", CultureInfo.InvariantCulture), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 15),
                ])
            .AddValueCase(
                DisplaySurface.Any,
                context => "Matrix4x4");
    }

    // ── WebProxy ─────────────────────────────────────────────────────────

    private static DisplayProfile CreateWebProxyProfile()
    {
        return DisplayProfile
            .For<WebProxy>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Address", row => ((WebProxy)row).Address?.ToString() ?? "<none>", MinWidth: 8, MaxWidth: 48, Priority: 0, CanHide: false),
                    new DisplayTableColumn("BypassLocal", row => ((WebProxy)row).BypassProxyOnLocal, MinWidth: 5, MaxWidth: 5, Priority: 10),
                    new DisplayTableColumn("BypassList", row => ((WebProxy)row).BypassList.Length, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 20),
                    new DisplayTableColumn("Credentials", row => ((WebProxy)row).Credentials is not null, MinWidth: 5, MaxWidth: 5, Priority: 30),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var proxy = (WebProxy)context.Value;
                    return proxy.Address?.ToString() ?? "<no address>";
                });
    }
}
