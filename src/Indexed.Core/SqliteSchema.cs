namespace Indexed.Core;

/// <summary>
/// Schema DDL and version constants for the per-repo <c>index.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// Schema version 2 defines: <c>files</c> (authoritative path/sha/language
/// row), <c>code_fts</c> as a <em>contentless</em> FTS5 trigram index
/// (tokenizes at write time but stores no content — snippets are rehydrated
/// from the working tree at query time), <c>prose_fts</c> (FTS5
/// porter+unicode61, content stored), and a <c>meta</c> KV for schema
/// version and repo identity.
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
    /// </list>
    /// </remarks>
    public const int Version = 2;

    /// <summary>
    /// Full DDL executed on a fresh <c>index.db</c>.
    /// </summary>
    public const string Ddl = """
        CREATE TABLE files (
            file_id     INTEGER PRIMARY KEY,
            path        TEXT UNIQUE NOT NULL,
            mtime_utc   INTEGER NOT NULL,
            size_bytes  INTEGER NOT NULL,
            sha256      BLOB NOT NULL,
            language    TEXT,
            indexed_at  INTEGER NOT NULL
        );
        CREATE INDEX files_path_glob ON files(path);

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
}
