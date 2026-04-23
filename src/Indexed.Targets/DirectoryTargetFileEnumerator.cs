using System;
using System.Collections.Generic;
using System.IO;

namespace Indexed.Targets;

internal static class DirectoryTargetFileEnumerator
{
    public static IEnumerable<string> EnumerateFilesRecursive(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(TargetPathUtilities.NormalizeDirectoryPath(rootPath));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] subdirectories;
            string[] files;
            try
            {
                subdirectories = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            Array.Sort(files, TargetPathUtilities.PathComparer);
            foreach (var file in files)
                yield return file;

            Array.Sort(subdirectories, TargetPathUtilities.PathComparer);
            for (var i = subdirectories.Length - 1; i >= 0; i--)
            {
                var directory = subdirectories[i];
                try
                {
                    var attributes = File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                pending.Push(directory);
            }
        }
    }
}
