namespace Tosh.Tome;

/// <summary>
/// Maps a file path's extension to a short, properly-cased language
/// label shown in the status bar. Unknown extensions fall back to a
/// title-cased version of the extension, or <c>Plain</c> when the file
/// has no extension.
/// </summary>
internal static class LanguageInfo
{
    private static readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".tosh"] = "TōSh",
        [".tome"] = "Tōme",
        [".cs"] = "C#",
        [".csproj"] = "MSBuild",
        [".fs"] = "F#",
        [".fsproj"] = "MSBuild",
        [".vb"] = "VB.NET",
        [".csx"] = "C# Script",
        [".js"] = "JavaScript",
        [".jsx"] = "JSX",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".ts"] = "TypeScript",
        [".tsx"] = "TSX",
        [".py"] = "Python",
        [".pyi"] = "Python",
        [".rb"] = "Ruby",
        [".go"] = "Go",
        [".rs"] = "Rust",
        [".c"] = "C",
        [".h"] = "C",
        [".cpp"] = "C++",
        [".cc"] = "C++",
        [".cxx"] = "C++",
        [".hpp"] = "C++",
        [".hh"] = "C++",
        [".java"] = "Java",
        [".kt"] = "Kotlin",
        [".kts"] = "Kotlin",
        [".scala"] = "Scala",
        [".swift"] = "Swift",
        [".m"] = "Objective-C",
        [".mm"] = "Objective-C++",
        [".sh"] = "Shell",
        [".bash"] = "Bash",
        [".zsh"] = "Zsh",
        [".fish"] = "Fish",
        [".ps1"] = "PowerShell",
        [".md"] = "Markdown",
        [".markdown"] = "Markdown",
        [".rst"] = "reStructuredText",
        [".org"] = "Org",
        [".txt"] = "Text",
        [".log"] = "Log",
        [".json"] = "JSON",
        [".jsonc"] = "JSONC",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".toml"] = "TOML",
        [".ini"] = "INI",
        [".cfg"] = "Config",
        [".conf"] = "Config",
        [".xml"] = "XML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "SCSS",
        [".sass"] = "Sass",
        [".less"] = "Less",
        [".vue"] = "Vue",
        [".svelte"] = "Svelte",
        [".lua"] = "Lua",
        [".sql"] = "SQL",
        [".dockerfile"] = "Dockerfile",
        [".tex"] = "LaTeX",
        [".bib"] = "BibTeX",
        [".r"] = "R",
        [".jl"] = "Julia",
        [".pl"] = "Perl",
        [".php"] = "PHP",
        [".dart"] = "Dart",
        [".zig"] = "Zig",
        [".nim"] = "Nim",
        [".elm"] = "Elm",
        [".ex"] = "Elixir",
        [".exs"] = "Elixir",
        [".erl"] = "Erlang",
        [".clj"] = "Clojure",
        [".hs"] = "Haskell",
        [".ml"] = "OCaml",
        [".mli"] = "OCaml",
        [".sln"] = "Solution",
        [".slnx"] = "Solution",
        [".gradle"] = "Gradle",
        [".cmake"] = "CMake",
        [".diff"] = "Diff",
        [".patch"] = "Patch",
    };

    private static readonly Dictionary<string, string> _byBasename = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "Dockerfile",
        ["Containerfile"] = "Dockerfile",
        ["Makefile"] = "Make",
        ["GNUmakefile"] = "Make",
        ["Rakefile"] = "Ruby",
        ["Gemfile"] = "Ruby",
        ["CMakeLists.txt"] = "CMake",
        ["Justfile"] = "Just",
    };

    public static string Resolve(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "Plain";
        var name = Path.GetFileName(filePath);
        if (_byBasename.TryGetValue(name, out var basenameLabel)) return basenameLabel;
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return "Plain";
        if (_byExtension.TryGetValue(ext, out var label)) return label;
        var bare = ext.TrimStart('.');
        if (bare.Length == 0) return "Plain";
        return char.ToUpperInvariant(bare[0]) + bare.Substring(1);
    }
}
