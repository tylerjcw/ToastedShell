using System.Runtime.InteropServices;

namespace Tosh.Tome.TreeSitter;

/// <summary>
/// Minimal P/Invoke shim against the system-installed
/// <c>libtree-sitter.so</c> (Arch Linux package <c>tree-sitter</c>).
/// Covers parser create/parse, tree root, and cursor-based walking —
/// enough to drive a node-type-keyed colorizer without bringing in a
/// managed grammar binding crate.
/// </summary>
internal static class TreeSitterNative
{
    private const string Lib = "tree-sitter";

    // TSNode is 4*u32 + 2 pointers = 32 bytes on 64-bit. Layout must match
    // the C definition exactly so the struct-by-value calls work.
    [StructLayout(LayoutKind.Sequential)]
    public struct TSNode
    {
        public uint Context0, Context1, Context2, Context3;
        public IntPtr Id;
        public IntPtr Tree;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TSPoint
    {
        public uint Row;
        public uint Column;
    }

    // TSTreeCursor is 2 pointers + 3*u32 = 28 bytes; pad to next aligned size.
    [StructLayout(LayoutKind.Sequential)]
    public struct TSTreeCursor
    {
        public IntPtr Tree;
        public IntPtr Id;
        public uint Context0, Context1, Context2;
    }

    [DllImport(Lib)] public static extern IntPtr ts_parser_new();
    [DllImport(Lib)] public static extern void ts_parser_delete(IntPtr parser);
    [DllImport(Lib)] public static extern bool ts_parser_set_language(IntPtr parser, IntPtr language);

    [DllImport(Lib)]
    public static extern IntPtr ts_parser_parse_string(IntPtr parser, IntPtr oldTree, IntPtr text, uint length);

    [DllImport(Lib)] public static extern void ts_tree_delete(IntPtr tree);
    [DllImport(Lib)] public static extern TSNode ts_tree_root_node(IntPtr tree);

    [DllImport(Lib)] public static extern IntPtr ts_node_type(TSNode node);
    [DllImport(Lib)] public static extern bool ts_node_is_named(TSNode node);
    [DllImport(Lib)] public static extern uint ts_node_start_byte(TSNode node);
    [DllImport(Lib)] public static extern uint ts_node_end_byte(TSNode node);
    [DllImport(Lib)] public static extern TSPoint ts_node_start_point(TSNode node);
    [DllImport(Lib)] public static extern TSPoint ts_node_end_point(TSNode node);
    [DllImport(Lib)] public static extern uint ts_node_child_count(TSNode node);
    [DllImport(Lib)] public static extern TSNode ts_node_parent(TSNode node);

    [DllImport(Lib)] public static extern TSTreeCursor ts_tree_cursor_new(TSNode node);
    [DllImport(Lib)] public static extern void ts_tree_cursor_delete(ref TSTreeCursor cursor);
    [DllImport(Lib)] public static extern bool ts_tree_cursor_goto_first_child(ref TSTreeCursor cursor);
    [DllImport(Lib)] public static extern bool ts_tree_cursor_goto_next_sibling(ref TSTreeCursor cursor);
    [DllImport(Lib)] public static extern bool ts_tree_cursor_goto_parent(ref TSTreeCursor cursor);
    [DllImport(Lib)] public static extern TSNode ts_tree_cursor_current_node(ref TSTreeCursor cursor);
}
