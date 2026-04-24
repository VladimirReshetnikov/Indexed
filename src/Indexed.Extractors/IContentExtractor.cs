using System.Collections.Generic;

namespace Indexed.Extractors;

/// <summary>
/// Extracts prose spans from a decoded source file.
/// </summary>
public interface IContentExtractor
{
    /// <summary>
    /// Extract prose spans from <paramref name="content"/>.
    /// </summary>
    IReadOnlyList<ExtractedProseSpan> Extract(string logicalPath, string content);
}
