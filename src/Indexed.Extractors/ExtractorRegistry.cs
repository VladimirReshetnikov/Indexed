using System;
using System.Collections.Generic;
using System.IO;

namespace Indexed.Extractors;

/// <summary>
/// Maps file extensions to prose extractors.
/// </summary>
public sealed class ExtractorRegistry
{
    private readonly IReadOnlyDictionary<string, IContentExtractor> _byExtension;

    private ExtractorRegistry(IReadOnlyDictionary<string, IContentExtractor> byExtension)
    {
        _byExtension = byExtension;
    }

    public static ExtractorRegistry BuildDefault()
    {
        var markdown = new MarkdownExtractor();
        var plainText = new PlainTextExtractor();
        var csharp = new RoslynCSharpExtractor();
        var cFamily = new CFamilyRegexCommentExtractor();
        var fsharp = new FSharpRegexCommentExtractor();
        var hash = new HashLineCommentExtractor();
        var powershell = new PowerShellCommentExtractor();
        var sql = new SqlCommentExtractor();
        var xmlHtml = new XmlHtmlCommentExtractor();

        var map = new Dictionary<string, IContentExtractor>(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = markdown,
            [".markdown"] = markdown,
            [".mdown"] = markdown,
            [".mkd"] = markdown,

            [".txt"] = plainText,
            [".rst"] = plainText,
            [".adoc"] = plainText,

            [".cs"] = csharp,

            [".c"] = cFamily,
            [".cc"] = cFamily,
            [".cpp"] = cFamily,
            [".cxx"] = cFamily,
            [".h"] = cFamily,
            [".hh"] = cFamily,
            [".hpp"] = cFamily,
            [".hxx"] = cFamily,
            [".java"] = cFamily,
            [".js"] = cFamily,
            [".jsx"] = cFamily,
            [".ts"] = cFamily,
            [".tsx"] = cFamily,
            [".css"] = cFamily,
            [".scss"] = cFamily,
            [".less"] = cFamily,
            [".go"] = cFamily,
            [".rs"] = cFamily,
            [".swift"] = cFamily,

            [".fs"] = fsharp,
            [".fsi"] = fsharp,
            [".fsx"] = fsharp,

            [".py"] = hash,
            [".rb"] = hash,
            [".sh"] = hash,
            [".bash"] = hash,
            [".zsh"] = hash,
            [".yaml"] = hash,
            [".yml"] = hash,
            [".toml"] = hash,

            [".ps1"] = powershell,
            [".psm1"] = powershell,
            [".psd1"] = powershell,

            [".sql"] = sql,

            [".xml"] = xmlHtml,
            [".html"] = xmlHtml,
            [".htm"] = xmlHtml,
            [".xaml"] = xmlHtml,
            [".svg"] = xmlHtml,
            [".csproj"] = xmlHtml,
            [".props"] = xmlHtml,
            [".targets"] = xmlHtml,
            [".config"] = xmlHtml,
        };

        return new ExtractorRegistry(map);
    }

    public IReadOnlyList<ExtractedProseSpan> Extract(string logicalPath, string content)
    {
        if (string.IsNullOrEmpty(logicalPath))
            throw new ArgumentException("logicalPath is required", nameof(logicalPath));

        var extension = Path.GetExtension(logicalPath);
        if (string.IsNullOrEmpty(extension))
            return [];

        return _byExtension.TryGetValue(extension, out var extractor)
            ? extractor.Extract(logicalPath, content)
            : [];
    }
}
