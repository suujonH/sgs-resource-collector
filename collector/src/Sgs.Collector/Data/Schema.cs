namespace Sgs.Collector.Data;

internal static class Schema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY, trigger_kind TEXT NOT NULL, status TEXT NOT NULL,
            started_at TEXT NOT NULL, finished_at TEXT NULL, stats_json TEXT NULL, error TEXT NULL,
            sync_status TEXT NULL, sync_error TEXT NULL);

        CREATE TABLE IF NOT EXISTS resources (
            id INTEGER PRIMARY KEY, resource_key TEXT NOT NULL UNIQUE,
            parent_id INTEGER NULL REFERENCES resources(id), source_path TEXT NULL, fetch_url TEXT NULL,
            latest_version INTEGER NOT NULL DEFAULT 0, latest_hash TEXT NULL,
            etag TEXT NULL, last_modified TEXT NULL, processing_rev INTEGER NULL,
            first_seen_at TEXT NOT NULL, last_seen_run INTEGER NULL,
            last_checked_at TEXT NULL, last_outcome TEXT NULL, last_http_status INTEGER NULL,
            last_error TEXT NULL, last_success_at TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_resources_parent ON resources(parent_id);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_resources_member ON resources(parent_id, source_path) WHERE source_path IS NOT NULL;

        CREATE TABLE IF NOT EXISTS versions (
            resource_id INTEGER NOT NULL REFERENCES resources(id), version INTEGER NOT NULL,
            hash TEXT NOT NULL, size INTEGER NOT NULL, content_type TEXT NOT NULL,
            collected_at TEXT NOT NULL, run_id INTEGER NOT NULL REFERENCES runs(id),
            synced INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (resource_id, version));
        CREATE INDEX IF NOT EXISTS ix_versions_run ON versions(run_id);
        CREATE INDEX IF NOT EXISTS ix_versions_unsynced ON versions(resource_id, version) WHERE synced = 0;

        CREATE TABLE IF NOT EXISTS links (
            resource_id INTEGER NOT NULL REFERENCES resources(id), url TEXT NOT NULL, source TEXT NOT NULL,
            PRIMARY KEY (resource_id, url)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS blobs (hash TEXT PRIMARY KEY, size INTEGER NOT NULL, uploaded_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS failures (
            id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL REFERENCES runs(id),
            resource_id INTEGER NOT NULL REFERENCES resources(id), http_status INTEGER NULL,
            error TEXT NOT NULL, had_history INTEGER NOT NULL, occurred_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_failures_run ON failures(run_id);
        CREATE INDEX IF NOT EXISTS ix_failures_resource ON failures(resource_id);

        CREATE TABLE IF NOT EXISTS header_rules (
            id INTEGER PRIMARY KEY, pattern TEXT NOT NULL, headers_json TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1, note TEXT NULL, updated_at TEXT NOT NULL);
        """;
}
