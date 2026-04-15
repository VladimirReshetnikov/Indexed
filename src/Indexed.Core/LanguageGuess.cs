using System;
using System.IO;

namespace Indexed.Core;

/// <summary>
/// Extension-driven language classifier used to populate <c>files.language</c>.
/// Returns <c>null</c> for files the classifier doesn't recognize — that
/// column is advisory only and Stage 2 doesn't filter by it.
/// </summary>
/// <remarks>
/// Kept deliberately small: Stage 2 persists this for later stages to inspect,
/// but it never drives indexing decisions. Stage 3's extractor selection may
/// consult it; Stage 6 can replace this with a real detector (e.g. Linguist).
/// </remarks>
internal static class LanguageGuess
{
    public static string? FromPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        return ext.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".fs" or ".fsx" => "fsharp",
            ".vb" => "vbnet",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
            ".py" => "python",
            ".rs" => "rust",
            ".go" => "go",
            ".java" => "java",
            ".kt" or ".kts" => "kotlin",
            ".rb" => "ruby",
            ".php" => "php",
            ".swift" => "swift",
            ".c" or ".h" => "c",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "cpp",
            ".md" or ".markdown" => "markdown",
            ".txt" or ".rst" => "plain-text",
            ".json" => "json",
            ".xml" or ".xaml" or ".csproj" or ".sln" or ".axaml" => "xml",
            ".yml" or ".yaml" => "yaml",
            ".toml" => "toml",
            ".sh" or ".bash" => "shell",
            ".ps1" or ".psm1" or ".psd1" => "powershell",
            ".il" => "cil",
            ".sql" => "sql",
            _ => null,
        };
    }
}
