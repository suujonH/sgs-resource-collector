import { DurableObject } from "cloudflare:workers";

export interface CatalogVersion {
	key: string;
	version: number;
	hash: string;
	size: number;
	contentType: string;
	collectedAt: string;
}

export interface ResolvedVersion {
	version: number;
	hash: string;
	size: number;
	contentType: string;
	collectedAt: string;
}

export interface ApplyResult {
	accepted: number;
	conflicts: { key: string; version: number; existingHash: string }[];
}

// sql.exec<T> 要求 T 带隐式索引签名，所以行类型用 type 而不是 interface
type VersionRow = { version: number; hash: string; size: number; content_type: string; collected_at: string };
type ExportRow = { resource_key: string; version: number; hash: string; size: number; content_type: string; collected_at: string };
type ListRow = { k: string; latest: number; version: number; collected_at: string };
type CountRow = { c: number };

const CATALOG_NAME = "catalog";

export function getCatalog(env: Env): DurableObjectStub<Catalog> {
	return env.CATALOG.getByName(CATALOG_NAME);
}

// R2 按内容寻址：对象 key = blobs/{xxh128}
export function blobKey(hash: string): string {
	return "blobs/" + hash;
}

function toResolved(row: VersionRow): ResolvedVersion {
	return { version: row.version, hash: row.hash, size: row.size, contentType: row.content_type, collectedAt: row.collected_at };
}

export class Catalog extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		this.ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS resources (resource_key TEXT PRIMARY KEY, latest INTEGER NOT NULL)");
		this.ctx.storage.sql.exec(
			`CREATE TABLE IF NOT EXISTS versions (resource_key TEXT NOT NULL, version INTEGER NOT NULL, hash TEXT NOT NULL,
				size INTEGER NOT NULL, content_type TEXT NOT NULL, collected_at TEXT NOT NULL, PRIMARY KEY (resource_key, version))`,
		);
	}

	// (key, 版本) → 哈希一经写入永不改变：相同哈希幂等，不同哈希记为冲突且不覆盖
	applyVersions(rows: CatalogVersion[]): ApplyResult {
		const sql = this.ctx.storage.sql;
		return this.ctx.storage.transactionSync(() => {
			let accepted = 0;
			const conflicts: ApplyResult["conflicts"] = [];
			for (const row of rows) {
				const existing = sql
					.exec<{ hash: string }>("SELECT hash FROM versions WHERE resource_key = ? AND version = ?", row.key, row.version)
					.toArray();
				if (existing.length === 0) {
					sql.exec(
						"INSERT INTO versions (resource_key, version, hash, size, content_type, collected_at) VALUES (?, ?, ?, ?, ?, ?)",
						row.key,
						row.version,
						row.hash,
						row.size,
						row.contentType,
						row.collectedAt,
					);
				} else if (existing[0].hash !== row.hash) {
					conflicts.push({ key: row.key, version: row.version, existingHash: existing[0].hash });
					continue;
				}
				sql.exec(
					"INSERT INTO resources (resource_key, latest) VALUES (?, ?) ON CONFLICT (resource_key) DO UPDATE SET latest = MAX(latest, excluded.latest)",
					row.key,
					row.version,
				);
				accepted++;
			}
			return { accepted, conflicts };
		});
	}

	// version 为 null 时取最新版；0 行时 .one() 会抛异常，所以统一用 toArray()
	resolve(key: string, version: number | null): ResolvedVersion | null {
		const sql = this.ctx.storage.sql;
		let rows: VersionRow[];
		if (version === null) {
			rows = sql
				.exec<VersionRow>(
					`SELECT v.version, v.hash, v.size, v.content_type, v.collected_at FROM resources r
					JOIN versions v ON v.resource_key = r.resource_key AND v.version = r.latest WHERE r.resource_key = ?`,
					key,
				)
				.toArray();
		} else {
			rows = sql
				.exec<VersionRow>(
					"SELECT version, hash, size, content_type, collected_at FROM versions WHERE resource_key = ? AND version = ?",
					key,
					version,
				)
				.toArray();
		}
		return rows.length === 0 ? null : toResolved(rows[0]);
	}

	info(key: string): { key: string; latest: number; versions: ResolvedVersion[] } | null {
		const sql = this.ctx.storage.sql;
		const latest = sql.exec<{ latest: number }>("SELECT latest FROM resources WHERE resource_key = ?", key).toArray();
		if (latest.length === 0) {
			return null;
		}
		const versions = sql
			.exec<VersionRow>(
				"SELECT version, hash, size, content_type, collected_at FROM versions WHERE resource_key = ? ORDER BY version",
				key,
			)
			.toArray()
			.map(toResolved);
		return { key, latest: latest[0].latest, versions };
	}

	// JSON 在 DO 内生成：DO 有 30 s CPU，而免费版 Worker 只有 10 ms，Worker 只转发字符串
	listJson(): string {
		const cursor = this.ctx.storage.sql.exec<ListRow>(
			`SELECT r.resource_key AS k, r.latest, v.version, v.collected_at FROM resources r
			JOIN versions v ON v.resource_key = r.resource_key ORDER BY r.resource_key, v.version`,
		);
		const resources: { key: string; latest: number; versions: { version: number; collectedAt: string }[] }[] = [];
		let current: (typeof resources)[number] | null = null;
		for (const row of cursor) {
			if (current === null || current.key !== row.k) {
				current = { key: row.k, latest: row.latest, versions: [] };
				resources.push(current);
			}
			current.versions.push({ version: row.version, collectedAt: row.collected_at });
		}
		return JSON.stringify({ generatedAt: new Date().toISOString(), count: resources.length, resources });
	}

	exportJson(afterKey: string, afterVersion: number, limit: number): string {
		const rows = this.ctx.storage.sql
			.exec<ExportRow>(
				`SELECT resource_key, version, hash, size, content_type, collected_at FROM versions
				WHERE resource_key > ? OR (resource_key = ? AND version > ?) ORDER BY resource_key, version LIMIT ?`,
				afterKey,
				afterKey,
				afterVersion,
				limit,
			)
			.toArray()
			.map((row) => ({
				key: row.resource_key,
				version: row.version,
				hash: row.hash,
				size: row.size,
				contentType: row.content_type,
				collectedAt: row.collected_at,
			}));
		const last = rows[rows.length - 1];
		const next = rows.length === limit && last !== undefined ? { key: last.key, version: last.version } : null;
		return JSON.stringify({ rows, next });
	}

	counts(): { resources: number; versions: number } {
		const sql = this.ctx.storage.sql;
		const resources = sql.exec<CountRow>("SELECT COUNT(*) AS c FROM resources").toArray()[0].c;
		const versions = sql.exec<CountRow>("SELECT COUNT(*) AS c FROM versions").toArray()[0].c;
		return { resources, versions };
	}
}
