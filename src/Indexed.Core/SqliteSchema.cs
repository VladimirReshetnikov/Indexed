namespace Indexed.Core;

/// <summary>
/// Schema DDL and version constants for the per-repo <c>index.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2 defines schema version 1: <c>files</c>, <c>code_fts</c> (FTS5
/// trigram), <c>prose_fts</c> (FTS5 porter+unicode61, populated by Stage 3
/// extractors), and a <c>meta</c> KV holding the schema version and repo
/// identity. The <c>prose_fts</c> table is created eagerly so Stage 3 can
/// start writing to it without a schema migration.
/// </para>
/// <para>
/// Schema changes are breaking: if <see cref="Version"/> is bumped,
/// <see cref="SqliteIndex"/> deletes the existing <c>index.db</c> and recreates
/// it from scratch. There is no in-place migration path for the v1 cycle —
/// rebuilds are cheap (&lt; 60 s for this repo) and guaranteed-correct.
/// </para>
/// </remarks>
public static class SqliteSchema
{
    /// <summary>Current schema version. Stored in <c>meta.schema_version</c>.</summary>
    public const int Version = 1;

    /// <summary>
    /// Full DDL executed on a fresh <c>index.db</c>. Matches the layout in
    /// <c>Indexed-Architecture-Proposal.md §4.2</c> verbatim.
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
