using System.Runtime.InteropServices;

namespace Tosh.Tome.TreeSitter;

/// <summary>
/// Maps file extensions to tree-sitter grammars installed as shared
/// libraries on the host (typically <c>/usr/lib/libtree-sitter-X.so</c>
/// from the Arch <c>tree-sitter-grammars</c> meta-package). Each
/// grammar exposes a <c>tree_sitter_X()</c> entry point that returns
/// an opaque <c>TSLanguage*</c>.
/// </summary>
internal static class TreeSitterGrammarRegistry
{
    private delegate IntPtr LanguageFn();

    private sealed record GrammarSpec(string Library, string EntryPoint);

    private static readonly Dictionary<string, GrammarSpec> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Already shipped in Arch's tree-sitter-grammars meta-package.
        [".py"] = new("tree-sitter-python", "tree_sitter_python"),
        [".pyi"] = new("tree-sitter-python", "tree_sitter_python"),
        [".js"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        [".mjs"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        [".cjs"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        [".jsx"] = new("tree-sitter-javascript", "tree_sitter_javascript"),
        [".lua"] = new("tree-sitter-lua", "tree_sitter_lua"),
        [".rs"] = new("tree-sitter-rust", "tree_sitter_rust"),
        [".c"] = new("tree-sitter-c", "tree_sitter_c"),
        [".h"] = new("tree-sitter-c", "tree_sitter_c"),
        [".sh"] = new("tree-sitter-bash", "tree_sitter_bash"),
        [".bash"] = new("tree-sitter-bash", "tree_sitter_bash"),
        [".zsh"] = new("tree-sitter-bash", "tree_sitter_bash"),
        [".md"] = new("tree-sitter-markdown", "tree_sitter_markdown"),
        [".markdown"] = new("tree-sitter-markdown", "tree_sitter_markdown"),

        // Preemptive — light up automatically when the grammar is
        // installed (e.g. `pacman -S tree-sitter-go`). Silently no-op
        // when missing.

        // C-family
        [".cpp"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        [".cxx"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        [".cc"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        [".hpp"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        [".hh"] = new("tree-sitter-cpp", "tree_sitter_cpp"),
        [".cs"] = new("tree-sitter-c-sharp", "tree_sitter_c_sharp"),
        [".csx"] = new("tree-sitter-c-sharp", "tree_sitter_c_sharp"),
        [".java"] = new("tree-sitter-java", "tree_sitter_java"),
        [".kt"] = new("tree-sitter-kotlin", "tree_sitter_kotlin"),
        [".kts"] = new("tree-sitter-kotlin", "tree_sitter_kotlin"),
        [".swift"] = new("tree-sitter-swift", "tree_sitter_swift"),
        [".m"] = new("tree-sitter-objc", "tree_sitter_objc"),
        [".scala"] = new("tree-sitter-scala", "tree_sitter_scala"),
        [".sc"] = new("tree-sitter-scala", "tree_sitter_scala"),

        // Systems & low-level
        [".go"] = new("tree-sitter-go", "tree_sitter_go"),
        [".zig"] = new("tree-sitter-zig", "tree_sitter_zig"),
        [".odin"] = new("tree-sitter-odin", "tree_sitter_odin"),
        [".nim"] = new("tree-sitter-nim", "tree_sitter_nim"),
        [".d"] = new("tree-sitter-d", "tree_sitter_d"),
        [".v"] = new("tree-sitter-v", "tree_sitter_v"),

        // Functional
        [".hs"] = new("tree-sitter-haskell", "tree_sitter_haskell"),
        [".ml"] = new("tree-sitter-ocaml", "tree_sitter_ocaml"),
        [".mli"] = new("tree-sitter-ocaml", "tree_sitter_ocaml_interface"),
        [".fs"] = new("tree-sitter-fsharp", "tree_sitter_fsharp"),
        [".fsx"] = new("tree-sitter-fsharp", "tree_sitter_fsharp"),
        [".ex"] = new("tree-sitter-elixir", "tree_sitter_elixir"),
        [".exs"] = new("tree-sitter-elixir", "tree_sitter_elixir"),
        [".erl"] = new("tree-sitter-erlang", "tree_sitter_erlang"),
        [".clj"] = new("tree-sitter-clojure", "tree_sitter_clojure"),
        [".cljs"] = new("tree-sitter-clojure", "tree_sitter_clojure"),
        [".elm"] = new("tree-sitter-elm", "tree_sitter_elm"),
        [".rkt"] = new("tree-sitter-racket", "tree_sitter_racket"),

        // Web — JS/TS family
        [".ts"] = new("tree-sitter-typescript", "tree_sitter_typescript"),
        [".tsx"] = new("tree-sitter-typescript", "tree_sitter_tsx"),
        [".vue"] = new("tree-sitter-vue", "tree_sitter_vue"),
        [".svelte"] = new("tree-sitter-svelte", "tree_sitter_svelte"),
        [".astro"] = new("tree-sitter-astro", "tree_sitter_astro"),

        // Web — markup & styles
        [".html"] = new("tree-sitter-html", "tree_sitter_html"),
        [".htm"] = new("tree-sitter-html", "tree_sitter_html"),
        [".xhtml"] = new("tree-sitter-html", "tree_sitter_html"),
        [".xml"] = new("tree-sitter-xml", "tree_sitter_xml"),
        [".css"] = new("tree-sitter-css", "tree_sitter_css"),
        [".scss"] = new("tree-sitter-scss", "tree_sitter_scss"),

        // Data / config
        [".json"] = new("tree-sitter-json", "tree_sitter_json"),
        [".jsonc"] = new("tree-sitter-json", "tree_sitter_json"),
        [".yaml"] = new("tree-sitter-yaml", "tree_sitter_yaml"),
        [".yml"] = new("tree-sitter-yaml", "tree_sitter_yaml"),
        [".toml"] = new("tree-sitter-toml", "tree_sitter_toml"),
        [".ini"] = new("tree-sitter-ini", "tree_sitter_ini"),

        // Scripting / dynamic
        [".rb"] = new("tree-sitter-ruby", "tree_sitter_ruby"),
        [".pl"] = new("tree-sitter-perl", "tree_sitter_perl"),
        [".php"] = new("tree-sitter-php", "tree_sitter_php"),
        [".r"] = new("tree-sitter-r", "tree_sitter_r"),
        [".jl"] = new("tree-sitter-julia", "tree_sitter_julia"),
        [".dart"] = new("tree-sitter-dart", "tree_sitter_dart"),
        [".tcl"] = new("tree-sitter-tcl", "tree_sitter_tcl"),
        [".fish"] = new("tree-sitter-fish", "tree_sitter_fish"),
        [".nu"] = new("tree-sitter-nu", "tree_sitter_nu"),
        [".ps1"] = new("tree-sitter-powershell", "tree_sitter_powershell"),

        // Data / query languages
        [".sql"] = new("tree-sitter-sql", "tree_sitter_sql"),
        [".graphql"] = new("tree-sitter-graphql", "tree_sitter_graphql"),
        [".gql"] = new("tree-sitter-graphql", "tree_sitter_graphql"),
        [".proto"] = new("tree-sitter-proto", "tree_sitter_proto"),
        [".regex"] = new("tree-sitter-regex", "tree_sitter_regex"),

        // Build / infra
        [".cmake"] = new("tree-sitter-cmake", "tree_sitter_cmake"),
        ["cmakelists.txt"] = new("tree-sitter-cmake", "tree_sitter_cmake"),
        [".dockerfile"] = new("tree-sitter-dockerfile", "tree_sitter_dockerfile"),
        [".mk"] = new("tree-sitter-make", "tree_sitter_make"),
        [".tf"] = new("tree-sitter-hcl", "tree_sitter_hcl"),
        [".hcl"] = new("tree-sitter-hcl", "tree_sitter_hcl"),
        [".nix"] = new("tree-sitter-nix", "tree_sitter_nix"),
        [".bazel"] = new("tree-sitter-bazel", "tree_sitter_bazel"),
        [".bzl"] = new("tree-sitter-starlark", "tree_sitter_starlark"),

        // Vim — already installed with Arch's tree-sitter-vim package.
        [".vim"] = new("tree-sitter-vim", "tree_sitter_vim"),

        // Misc
        [".tex"] = new("tree-sitter-latex", "tree_sitter_latex"),
        [".bib"] = new("tree-sitter-bibtex", "tree_sitter_bibtex"),
        [".diff"] = new("tree-sitter-diff", "tree_sitter_diff"),
        [".patch"] = new("tree-sitter-diff", "tree_sitter_diff"),
        [".gitignore"] = new("tree-sitter-gitignore", "tree_sitter_gitignore"),
        [".gitattributes"] = new("tree-sitter-gitattributes", "tree_sitter_gitattributes"),
        [".gitcommit"] = new("tree-sitter-gitcommit", "tree_sitter_gitcommit"),
        [".scm"] = new("tree-sitter-query", "tree_sitter_query"),
    };

    // Cache resolved TSLanguage pointers — they're owned by the shared
    // library and live for the process lifetime, so we never free them.
    private static readonly Dictionary<string, IntPtr> _languageCache = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>
    /// Attempts to resolve a TSLanguage pointer for the given file path.
    /// Returns <c>IntPtr.Zero</c> when no grammar is registered or when
    /// the library can't be loaded (missing package, dlopen failure).
    /// </summary>
    public static IntPtr Resolve(string filePath, out string? grammarName)
    {
        grammarName = null;
        if (string.IsNullOrEmpty(filePath)) return IntPtr.Zero;
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return IntPtr.Zero;
        if (!_byExtension.TryGetValue(ext, out var spec)) return IntPtr.Zero;
        grammarName = spec.EntryPoint.StartsWith("tree_sitter_", StringComparison.Ordinal)
            ? spec.EntryPoint.Substring("tree_sitter_".Length)
            : spec.EntryPoint;

        lock (_gate)
        {
            if (_languageCache.TryGetValue(spec.EntryPoint, out var cached)) return cached;
            IntPtr lang = IntPtr.Zero;

            // Try a series of library-name candidates. NativeLibrary.Load
            // on Linux does not always synthesise the `lib` prefix or the
            // `.so` suffix, and Arch's grammar packages ship both an
            // unversioned and a SONAME-versioned filename (e.g.
            // libtree-sitter-markdown.so / .so.14.0). Try them in order.
            var candidates = new[]
            {
                spec.Library,                       // "tree-sitter-markdown"
                "lib" + spec.Library + ".so",       // "libtree-sitter-markdown.so"
                "/usr/lib/lib" + spec.Library + ".so", // absolute fallback
            };

            Exception? lastError = null;
            foreach (var cand in candidates)
            {
                try
                {
                    var libHandle = NativeLibrary.Load(cand);
                    var fn = NativeLibrary.GetExport(libHandle, spec.EntryPoint);
                    var del = Marshal.GetDelegateForFunctionPointer<LanguageFn>(fn);
                    lang = del();
                    TreeSitterDebug.Log($"loaded {cand} → {spec.EntryPoint}() = 0x{lang.ToInt64():x}");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TreeSitterDebug.Log($"candidate {cand} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (lang == IntPtr.Zero && lastError != null)
                TreeSitterDebug.Log($"all candidates exhausted for {spec.EntryPoint}");

            _languageCache[spec.EntryPoint] = lang;
            return lang;
        }
    }

    public static bool HasGrammarFor(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _byExtension.ContainsKey(ext);
    }
}
