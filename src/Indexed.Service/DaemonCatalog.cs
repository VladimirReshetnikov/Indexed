using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Indexed.Service;

/// <summary>
/// Enumerates daemon descriptors stored under the Indexed app-data root.
/// </summary>
public static class DaemonCatalog
{
    public static IReadOnlyList<DaemonInfo> List(string? appDataBase = null)
    {
        var root = string.IsNullOrEmpty(appDataBase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Indexed")
            : appDataBase;

        if (!Directory.Exists(root))
            return Array.Empty<DaemonInfo>();

        var daemons = new List<DaemonInfo>();
        foreach (var targetDirectory in Directory.EnumerateDirectories(root))
        {
            var info = DaemonInfo.TryRead(Path.Combine(targetDirectory, "daemon.json"));
            if (info is not null)
                daemons.Add(info);
        }

        return daemons
            .OrderBy(static daemon => daemon.TargetId, StringComparer.Ordinal)
            .ToArray();
    }
}
