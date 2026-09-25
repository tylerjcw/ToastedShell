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
    internal static void RegisterSystemProfiles(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        registry.Register(CreateIpAddressProfile());
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
        registry.Register(CreateCpuInfoProfile());
        registry.Register(CreateCpuTopologyProfile());
        registry.Register(CreateCpuCacheProfile());
        registry.Register(CreateSystemCounterProfile());
        registry.Register(CreateEndPointProfile());
        registry.Register(CreateHttpRequestDefinitionProfile());
        registry.Register(CreateHttpResponseInfoProfile());
        registry.Register(CreateHttpFileServerHandleProfile());
        registry.Register(CreateHttpRequestMessageProfile());
        registry.Register(CreateHttpResponseMessageProfile());
        registry.Register(CreateCookieProfile());
        registry.Register(CreateCookieCollectionProfile());
        registry.Register(CreateCookieContainerProfile());
        registry.Register(CreateNetworkCredentialProfile());
        registry.Register(CreatePhysicalAddressProfile());
        registry.Register(CreateIpHostEntryProfile());
        registry.Register(CreateWebHeaderCollectionProfile());
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
        registry.Register(CreateHttpRequestHeadersProfile());
        registry.Register(CreateHttpResponseHeadersProfile());
        registry.Register(CreateHttpContentHeadersProfile());
        registry.Register(CreateHttpHeadersProfile());
        registry.Register(CreateHttpContentProfile());
        registry.Register(CreateUnameInfoProfile());
        registry.Register(CreateUserIdentityInfoProfile());
        registry.Register(CreateMountInfoProfile());
        registry.Register(CreatePingReplyInfoProfile());
        registry.Register(CreateEnvironmentVariableEntryProfile());
        registry.Register(CreateShellVariableEntryProfile());
        registry.Register(CreateOperatingSystemProfile());
        registry.Register(CreateArchitectureProfile());
        registry.Register(CreateRuntimeInformationProfile());
        registry.Register(CreateWebProxyProfile());
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

    private static string FormatPingDuration(TimeSpan? duration)
    {
        return duration is null
            ? string.Empty
            : $"{duration.Value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";
    }

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
