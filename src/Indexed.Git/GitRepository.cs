using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Indexed.Git;

/// <summary>
/// Status code for a single entry in <c>git diff-tree</c> output.
/// </summary>
public enum DiffStatus
{
    /// <summary>File added in the target tree.</summary>
    Added,
    /// <summary>File modified between the two trees.</summary>
    Modified,
    /// <summary>File deleted from the target tree.</summary>
    Deleted,
    /// <summary>File renamed (old path → new path).</summary>
    Renamed,
    /// <summary>File copied (source retained, new path created).</summary>
    Copied,
    /// <summary>File is unmerged (conflict). Occurs during merge conflicts.</summary>
    Unmerged,
}

/// <summary>
/// One entry from <c>git diff-tree -r --name-status -z</c>.
/// </summary>
/// <param name="Status">Kind of change.</param>
/// <param name="Path">Repository-relative path (forward-slash separators) of the affected file.
/// For renames/copies this is the <em>new</em> path.</param>
/// <param name="OldPath">For <see cref="DiffStatus.Renamed"/> and <see cref="DiffStatus.Copied"/>,
/// the original path. <c>null</c> for all other statuses.</param>
public sealed record DiffTreeEntry(DiffStatus Status, string Path, string? OldPath);

/// <summary>
/// Thin adapter over <c>git.exe</c> that provides the exact set of repository
/// queries Indexed needs: root discovery, HEAD/first-commit probing, the
/// union enumeration (<c>ls-files</c> plus <c>--others --exclude-standard</c>),
/// diff-tree between commits, and binary-attribute interrogation.
/// </summary>
/// <remarks>
/// <para>
/// Ownership boundary per <c>Indexed-Architecture-Proposal.md §6.1</c>: this
/// class knows nothing about spans, FTS5 rows, or schema versions — it only
/// surfaces git's view of the working tree. Callers upstream (indexer
/// pipeline, service layer) translate between git paths and index state.
/// </para>
/// <para>
/// Path shape: every path returned by this class is repository-relative with
/// forward-slash separators, matching git's native output. The constructor
/// normalizes <see cref="RepoRoot"/> to the operating system's canonical form
/// (backslashes on Windows).
/// </para>
/// <para>
/// Thread-safety: all methods launch fresh <c>git</c> subprocesses and hold
/// no shared mutable state; concurrent calls across threads are safe. Each
/// call is synchronous and blocks until git exits.
/// </para>
/// </remarks>
public sealed class GitRepository
{
    private const int MaxBinaryPeekBytes = 8192;

    // Files larger than this are classified binary without reading. Picked at
    // 50 MB to match the proposal's size-based skip; adjustable once the
    // indexer has real telemetry.
    private const long MaxIndexableFileBytes = 50L * 1024 * 1024;

    private GitRepository(string repoRoot)
    {
        RepoRoot = repoRoot;
    }

    /// <summary>
    /// Discover the git repository containing <paramref name="directory"/>
    /// and return a handle for querying it.
    /// </summary>
    /// <remarks>
    /// Walks upward with <c>git rev-parse --show-toplevel</c>. Throws
    /// <see cref="GitProcessException"/> when <paramref name="directory"/>
    /// does not lie inside a git working tree.
    /// </remarks>
    /// <param name="directory">Any directory inside the target repository.</param>
    /// <returns>A <see cref="GitRepository"/> rooted at the discovered root.</returns>
    public static GitRepository Open(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"directory does not exist: {directory}");

        var rawRoot = GitProcess
            .RunText(directory, new[] { "rev-parse", "--show-toplevel" })
            .Trim();

        if (rawRoot.Length == 0)
            throw new InvalidOperationException(
                $"git rev-parse --show-toplevel produced empty output for '{directory}'");

        // Normalize to OS-native separators for clean display and to match
        // paths other Indexed components compose from Path.Combine.
        var normalized = Path.GetFullPath(rawRoot);
        return new GitRepository(normalized);
    }

    /// <summary>
    /// Absolute path to the repository root, normalized to OS-native
    /// separators (backslashes on Windows, forward on POSIX).
    /// </summary>
    public string RepoRoot { get; }

    /// <summary>
    /// Current <c>HEAD</c> commit SHA-1 as a 40-character lowercase hex string.
    /// </summary>
    /// <remarks>
    /// Returns the empty string for an empty repository (no commits yet). The
    /// ripgrep-backed Stage 1 daemon uses this to populate the
    /// <see cref="Indexed.Abstractions.Freshness.CurrentHead"/> slot even
    /// when no index is present.
    /// </remarks>
    public string GetHeadSha()
    {
        try
        {
            return GitProcess
                .RunText(RepoRoot, new[] { "rev-parse", "HEAD" })
                .Trim();
        }
        catch (GitProcessException ex) when (IsUnbornHead(ex))
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Commit SHA-1 of the first (root) commit on the current branch.
    /// </summary>
    /// <remarks>
    /// Uses <c>git rev-list --max-parents=0 HEAD</c>. When the branch has
    /// multiple root commits (merged histories), the output ordering from
    /// git is reverse-chronological; we take the oldest as the stable
    /// identity seed for <c>repoId</c>.
    /// </remarks>
    /// <returns>
    /// The root commit SHA, or the empty string for an empty repository.
    /// </returns>
    public string GetFirstCommitSha()
    {
        try
        {
            var raw = GitProcess.RunText(
                RepoRoot,
                new[] { "rev-list", "--max-parents=0", "HEAD" });

            // git rev-list prints one SHA per line. Multiple root commits are
            // rare but possible (merged histories); take the last line, which
            // is the oldest in rev-list's reverse-chronological ordering.
            var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? string.Empty : lines[^1].Trim();
        }
        catch (GitProcessException ex) when (IsUnbornHead(ex))
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Enumerate every repository-relative path that is either tracked or
    /// present-but-not-ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Result is the set union of <c>git ls-files -z</c> (tracked) and
    /// <c>git ls-files -z --others --exclude-standard</c> (untracked and not
    /// ignored). This is the authoritative file set per the proposal §3:
    /// the indexer never reads a file outside this list.
    /// </para>
    /// <para>
    /// Paths are sorted lexicographically for stability across invocations.
    /// Binary files and files over the size cap are <em>not</em> filtered
    /// here — that is the indexer's responsibility via
    /// <see cref="IsLikelyBinary"/> and <see cref="GetBinaryAttrPaths"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> EnumerateFiles()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in RunLsFilesZ(new[] { "ls-files", "-z" }))
            set.Add(path);

        foreach (var path in RunLsFilesZ(new[]
        {
            "ls-files", "-z", "--others", "--exclude-standard",
        }))
            set.Add(path);

        var result = new string[set.Count];
        set.CopyTo(result);
        return result;
    }

    /// <summary>
    /// Heuristically classify whether the file at the given
    /// repository-relative path is likely non-text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checks two cheap signals: (1) file length exceeds the indexable size
    /// cap, or (2) the first 8 KiB contain a NUL byte. Both are strong
    /// indicators of a binary payload (images, archives, executables) and
    /// together catch every file format the indexer needs to skip.
    /// </para>
    /// <para>
    /// Returns <c>true</c> when the file does not exist, is a directory, or
    /// cannot be opened — the indexer treats "I could not read this" and "this
    /// is binary" identically: both are non-indexable.
    /// </para>
    /// </remarks>
    /// <param name="relativePath">Repository-relative POSIX path.</param>
    public bool IsLikelyBinary(string relativePath)
    {
        var full = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
                return true;
            if (info.Length > MaxIndexableFileBytes)
                return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }

        try
        {
            using var stream = File.OpenRead(full);
            Span<byte> buf = stackalloc byte[MaxBinaryPeekBytes];
            var read = stream.Read(buf);
            for (var i = 0; i < read; i++)
            {
                if (buf[i] == 0) return true;
            }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Return the set of repository-relative paths that <c>.gitattributes</c>
    /// explicitly marks as <c>binary</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invokes <c>git check-attr binary -z --stdin</c> with every path from
    /// <see cref="EnumerateFiles"/>. The batched form avoids one subprocess
    /// per file; the result is parsed from the <c>-z</c>-separated
    /// triple-record stream (<c>path\0attr\0value\0</c>).
    /// </para>
    /// <para>
    /// A path appears in the returned set only when the <c>binary</c>
    /// macro-attribute is explicitly <c>set</c>. Paths with <c>unset</c> /
    /// <c>unspecified</c> / anything else are excluded. The proposal treats
    /// this set as the authoritative override for <see cref="IsLikelyBinary"/>:
    /// a file listed here is binary even if the NUL-scan would miss it.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> GetBinaryAttrPaths()
    {
        var files = EnumerateFiles();
        if (files.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        // git check-attr --stdin expects paths separated by \n in normal mode;
        // with -z it expects them \0-separated and emits \0-separated output.
        var stdinBuf = new MemoryStream();
        foreach (var p in files)
        {
            var bytes = Encoding.UTF8.GetBytes(p);
            stdinBuf.Write(bytes, 0, bytes.Length);
            stdinBuf.WriteByte(0);
        }

        var outBytes = GitProcess.RunBytes(
            RepoRoot,
            new[] { "check-attr", "binary", "-z", "--stdin" },
            stdinBuf.ToArray());

        // Output: repeated triples of NUL-terminated fields: path, attr, value.
        var result = new HashSet<string>(StringComparer.Ordinal);
        var fields = SplitNullTerminated(outBytes);
        for (var i = 0; i + 2 < fields.Count; i += 3)
        {
            if (fields[i + 1] == "binary" && fields[i + 2] == "set")
                result.Add(fields[i]);
        }
        return result;
    }

    /// <summary>
    /// Compute the tree diff between two commits and return a structured entry
    /// per changed file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <c>git diff-tree -r --name-status --no-commit-id -z &lt;from&gt; &lt;to&gt;</c>.
    /// The <c>-z</c> flag produces NUL-delimited output that handles paths with
    /// spaces or special characters. Renames and copies include both old and new
    /// paths.
    /// </para>
    /// <para>
    /// Throws <see cref="GitProcessException"/> when either commit is unreachable
    /// (pruned by GC, shallow clone boundary, etc.). Callers should catch and
    /// fall back to a full rescan.
    /// </para>
    /// </remarks>
    /// <param name="fromCommit">Base commit (40-hex SHA or any tree-ish).</param>
    /// <param name="toCommit">Target commit.</param>
    /// <returns>One entry per changed path. Empty when the trees are identical.</returns>
    public IReadOnlyList<DiffTreeEntry> GetDiffTree(string fromCommit, string toCommit)
    {
        if (string.IsNullOrEmpty(fromCommit)) throw new ArgumentException("fromCommit is required", nameof(fromCommit));
        if (string.IsNullOrEmpty(toCommit)) throw new ArgumentException("toCommit is required", nameof(toCommit));

        var bytes = GitProcess.RunBytes(
            RepoRoot,
            new[] { "diff-tree", "-r", "--name-status", "--no-commit-id", "-z", fromCommit, toCommit });

        var fields = SplitNullTerminated(bytes);
        var result = new List<DiffTreeEntry>();

        for (var i = 0; i < fields.Count;)
        {
            var statusField = fields[i];
            if (statusField.Length == 0) { i++; continue; }

            var statusChar = statusField[0];
            switch (statusChar)
            {
                case 'A':
                    if (i + 1 >= fields.Count) goto done;
                    result.Add(new DiffTreeEntry(DiffStatus.Added, fields[i + 1], null));
                    i += 2;
                    break;

                case 'M':
                case 'T': // type change — treat as modification
                    if (i + 1 >= fields.Count) goto done;
                    result.Add(new DiffTreeEntry(DiffStatus.Modified, fields[i + 1], null));
                    i += 2;
                    break;

                case 'D':
                    if (i + 1 >= fields.Count) goto done;
                    result.Add(new DiffTreeEntry(DiffStatus.Deleted, fields[i + 1], null));
                    i += 2;
                    break;

                case 'R': // R100, R095, etc. — old path then new path
                    if (i + 2 >= fields.Count) goto done;
                    result.Add(new DiffTreeEntry(DiffStatus.Renamed, fields[i + 2], fields[i + 1]));
                    i += 3;
                    break;

                case 'C': // C100, C095, etc. — source path then new path
                    if (i + 2 >= fields.Count) goto done;
                    result.Add(new DiffTreeEntry(DiffStatus.Copied, fields[i + 2], fields[i + 1]));
                    i += 3;
                    break;

                case 'U': // unmerged — two-field entry: status + path
                    if (i + 1 >= fields.Count) goto done;
                    result.Add(new DiffTreeEntry(DiffStatus.Unmerged, fields[i + 1], null));
                    i += 2;
                    break;

                default:
                    // Unknown status letter (X, B, etc.). These are two-field
                    // entries (status + path) in git diff-tree output. Consume
                    // both fields to keep the parser synchronized.
                    if (i + 1 >= fields.Count) goto done;
                    i += 2;
                    break;
            }
        }

        done:
        return result;
    }

    /// <summary>
    /// List all untracked, non-ignored files in the working tree.
    /// </summary>
    /// <remarks>
    /// Uses <c>git ls-files --others --exclude-standard -z</c>. This is the
    /// same set already included in <see cref="EnumerateFiles"/>, exposed
    /// separately for the incremental reconciliation pass which needs to diff
    /// the untracked set against the index without re-enumerating tracked files.
    /// </remarks>
    public IReadOnlyList<string> GetUntrackedFiles()
    {
        return RunLsFilesZ(new[] { "ls-files", "-z", "--others", "--exclude-standard" });
    }

    /// <summary>
    /// Return the last-write time of <c>.git/index</c> as a cheap canary for
    /// whether any git operation has mutated the staging area or HEAD since
    /// the last check.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when the file does not exist (bare repo, unborn
    /// branch). The HeadPoller uses this to avoid spawning <c>git rev-parse
    /// HEAD</c> on every tick when nothing has changed.
    /// </remarks>
    public DateTimeOffset? GetIndexMtime()
    {
        // .git may be a file (gitdir pointer for worktrees/submodules); find
        // the actual git directory.
        var gitDir = Path.Combine(RepoRoot, ".git");
        string indexPath;
        if (File.Exists(gitDir))
        {
            // gitdir: <path> format — read the target
            try
            {
                var content = File.ReadAllText(gitDir).Trim();
                if (content.StartsWith("gitdir:", StringComparison.Ordinal))
                    indexPath = Path.Combine(RepoRoot, content.Substring("gitdir:".Length).Trim(), "index");
                else
                    indexPath = Path.Combine(gitDir, "index");
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        else
        {
            indexPath = Path.Combine(gitDir, "index");
        }

        try
        {
            var info = new FileInfo(indexPath);
            if (!info.Exists) return null;
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<string> RunLsFilesZ(string[] args)
    {
        var bytes = GitProcess.RunBytes(RepoRoot, args);
        return SplitNullTerminated(bytes);
    }

    // Split a NUL-separated UTF-8 byte buffer into individual strings. Trailing
    // NUL after the last field (which git always emits) produces no empty
    // trailing element.
    internal static List<string> SplitNullTerminated(ReadOnlySpan<byte> bytes)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                var len = i - start;
                if (len > 0)
                    result.Add(Encoding.UTF8.GetString(bytes.Slice(start, len)));
                else
                    result.Add(string.Empty);
                start = i + 1;
            }
        }
        if (start < bytes.Length)
            result.Add(Encoding.UTF8.GetString(bytes.Slice(start, bytes.Length - start)));
        return result;
    }

    private static bool IsUnbornHead(GitProcessException ex)
    {
        // git emits this verbatim when HEAD points at an unborn branch.
        return ex.Message.Contains("ambiguous argument 'HEAD'", StringComparison.Ordinal)
            || ex.Message.Contains("unknown revision", StringComparison.Ordinal);
    }
}
