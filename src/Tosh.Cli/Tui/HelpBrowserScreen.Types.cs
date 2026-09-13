using Tosh.Runtime;

namespace Tosh.Cli.Tui;

/// <summary>The value types the help browser renders through.</summary>
internal sealed partial class HelpBrowserScreen
{
    private enum HelpBrowserFocus
    {
        Search,
        List,
        Detail,
    }

    private enum HelpBrowserGroup
    {
        All,
        ToastedShell,
        ToastScript,
        Clr,
    }

    private enum HelpBrowserListEntryKind
    {
        SectionHeader,
        Topic,
        ClrAssembly,
        ClrNamespace,
        ClrType,
        ClrFilterToggle,
        ClrConstructor,
        ClrMember,
        ClrMethod,
        Up,
    }

    private readonly record struct HelpBrowserListEntry(
        HelpBrowserListEntryKind Kind,
        string Label,
        string RawLabel,
        string? TopicName,
        string? SectionKey,
        string? Value,
        bool IsCollapsed)
    {
        public static HelpBrowserListEntry SectionHeader(string label, string sectionKey, bool collapsed)
        {
            var marker = collapsed ? "▸ " : "▾ ";
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.SectionHeader,
                marker + label,
                label,
                TopicName: null,
                SectionKey: sectionKey,
                Value: null,
                IsCollapsed: collapsed);
        }

        public static HelpBrowserListEntry Topic(HelpSummary summary)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.Topic,
                "  " + summary.Name,
                summary.Name,
                TopicName: summary.Name,
                SectionKey: summary.Category,
                Value: null,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrAssembly(ClrAssemblyBrowseInfo assembly)
        {
            var label = $"{assembly.Name} [{assembly.Types.Count}T/{assembly.Namespaces.Count}N]";
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrAssembly,
                "  " + label,
                assembly.Name,
                TopicName: null,
                SectionKey: "Assemblies",
                Value: assembly.Name,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrNamespace(string namespaceName)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrNamespace,
                "  " + namespaceName,
                namespaceName,
                TopicName: null,
                SectionKey: "Namespaces",
                Value: namespaceName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrNamespaceTree(string namespaceName, string label, string sectionKey, bool collapsed)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrNamespace,
                label,
                namespaceName,
                TopicName: null,
                SectionKey: sectionKey,
                Value: namespaceName,
                IsCollapsed: collapsed);
        }

        public static HelpBrowserListEntry ClrType(Type type)
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrType,
                "  " + displayName,
                displayName,
                TopicName: displayName,
                SectionKey: "Types",
                Value: displayName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrType(Type type, string label)
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrType,
                "  " + label,
                displayName,
                TopicName: displayName,
                SectionKey: "Types",
                Value: displayName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrTypeLink(string label, Type type)
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrType,
                "  " + label,
                label,
                TopicName: displayName,
                SectionKey: "Navigation",
                Value: displayName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrFilterToggle(bool declaredOnly)
        {
            var label = declaredOnly ? "Declared Only: on" : "Declared Only: off";
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrFilterToggle,
                "  " + label,
                label,
                TopicName: null,
                SectionKey: "View Options",
                Value: declaredOnly ? "declared" : "all",
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrConstructor(string signature)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrConstructor,
                "  " + signature,
                signature,
                TopicName: null,
                SectionKey: "Constructors",
                Value: signature,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrMember(string value, string label)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrMember,
                "  " + label,
                label,
                TopicName: null,
                SectionKey: "Members",
                Value: value,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrMethodGroup(string methodName, string label)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrMethod,
                "  " + label,
                label,
                TopicName: null,
                SectionKey: "Methods",
                Value: methodName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry Up(string label, string sectionKey)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.Up,
                label,
                label,
                TopicName: null,
                SectionKey: sectionKey,
                Value: null,
                IsCollapsed: false);
        }
    }

    internal readonly record struct HelpDetailEntry(string Text, HelpDetailEntryKind Kind, int? RelatedIndex = null);

    internal enum HelpDetailEntryKind
    {
        Blank,
        Meta,
        Text,
        SectionHeading,
        Example,
        RelatedTopic,
    }

    private readonly record struct SectionDescriptor(string Key, string Label, int GroupOrder, int SectionOrder);

    private sealed record ClrAssemblyBrowseInfo(
        string Name,
        string FullName,
        IReadOnlyList<Type> Types,
        IReadOnlyList<string> Namespaces);

    private sealed record ClrBrowseIndex(
        IReadOnlyList<ClrAssemblyBrowseInfo> Assemblies,
        IReadOnlyDictionary<string, ClrAssemblyBrowseInfo> ByName,
        IReadOnlyList<Type> AllTypes);
}
