namespace Indexed.Core;

/// <summary>
/// Schema DDL and version constants for the per-target <c>index.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// Schema version 3 defines: <c>roots</c> (target roots persisted by absolute
/// path), <c>files</c> (authoritative logical-path/sha/language row),
/// <c>code_fts</c> as a <em>contentless</em> FTS5 trigram index (tokenizes at
/// write time but stores no content — snippets are rehydrated from the
/// working tree at query time), <c>prose_fts</c> (FTS5 porter+unicode61,
/// content stored), and a <c>meta</c> KV for schema version and compatibility
/// metadata.
/// </para>
/// <para>
/// Schema changes are breaking: if <see cref="Version"/> is bumped,
/// <see cref="SqliteIndex"/> deletes the existing <c>index.db</c> and recreates
/// it from scratch. There is no in-place migration path — rebuilds are cheap
/// (&lt; 60 s for this repo) and guaranteed-correct.
/// </para>
/// </remarks>
public static class SqliteSchema
{
    /// <summary>Current schema version. Stored in <c>meta.schema_version</c>.</summary>
    /// <remarks>
    /// <para>
    /// Version history:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>1 — initial schema: <c>code_fts</c> stored full content.</description></item>
    ///   <item><description>2 — <c>code_fts</c> becomes contentless (<c>content = ''</c>);
    ///   snippet text is rehydrated from disk at query time. See
    ///   <c>Indexed-Size-Reduction-SafeNearTerm-Plan.md §Workstream C</c>.</description></item>
    ///   <item><description>3 — target-aware roots table plus
    ///   <c>files(root_id, relative_path, logical_path, ...)</c> so one index
    ///   can serve multi-root targets.</description></item>
    /// </list>
    /// </remarks>
    public const int Version = 3;

    /// <summary>
    /// Full DDL executed on a fresh <c>index.db</c>.
    /// </summary>
    public const string Ddl = """
        CREATE TABLE roots (
            root_id        INTEGER PRIMARY KEY,
            root_name      TEXT,
            absolute_path  TEXT UNIQUE NOT NULL,
            is_primary     INTEGER NOT NULL
        );

        CREATE TABLE files (
            file_id         INTEGER PRIMARY KEY,
            root_id         INTEGER NOT NULL REFERENCES roots(root_id),
            relative_path   TEXT NOT NULL,
            logical_path    TEXT UNIQUE NOT NULL,
            mtime_utc       INTEGER NOT NULL,
            size_bytes      INTEGER NOT NULL,
            sha256          BLOB NOT NULL,
            language        TEXT,
            indexed_at      INTEGER NOT NULL,
            UNIQUE(root_id, relative_path)
        );

        CREATE VIRTUAL TABLE code_fts USING fts5(
            content,
            content = '',
            contentless_delete = 1,
            tokenize = 'trigram'
        );

        CREATE VIRTUAL TABLE prose_fts USING fts5(
            content,
            kind         UNINDEXED,
            start_line   UNINDEXED,
            end_line     UNINDEXED,
            file_id      UNINDEXED,
            tokenize = 'porter unicode61'
        );

        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    public const string MetaKey_SchemaVersion = "schema_version";
    public const string MetaKey_RepoId = "repo_id";
    public const string MetaKey_IndexedHead = "indexed_head";
    public const string MetaKey_LastFullScanAt = "last_full_scan_at";
    public const string MetaKey_LastReconciliationAt = "last_reconciliation_at";
}
