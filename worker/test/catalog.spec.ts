import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import type { CatalogVersion } from "../src/catalog";

const H1 = "1".repeat(32);
const H2 = "2".repeat(32);
const H3 = "3".repeat(32);
const H4 = "4".repeat(32);

// 每个用例使用独立的 Durable Object 实例，计数与列表不受其他用例影响
function newCatalog() {
	return env.CATALOG.getByName(`catalog-spec-${crypto.randomUUID()}`);
}

function row(key: string, version: number, hash: string, overrides: Partial<CatalogVersion> = {}): CatalogVersion {
	return { key, version, hash, size: 100 + version, contentType: "text/plain", collectedAt: `2026-01-0${version}T00:00:00.000Z`, ...overrides };
}

describe("Catalog.resolve", () => {
	it("写入两个版本后，resolve(key, null) 返回 v2，resolve(key, 1) 返回 v1", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/resolve.json";
		await catalog.applyVersions([row(key, 1, H1)]);
		await catalog.applyVersions([row(key, 2, H2, { contentType: "application/json" })]);

		expect(await catalog.resolve(key, null)).toEqual({
			version: 2,
			hash: H2,
			size: 102,
			contentType: "application/json",
			collectedAt: "2026-01-02T00:00:00.000Z",
		});
		expect(await catalog.resolve(key, 1)).toEqual({
			version: 1,
			hash: H1,
			size: 101,
			contentType: "text/plain",
			collectedAt: "2026-01-01T00:00:00.000Z",
		});
	});

	it("不存在的 key 或版本返回 null，而不是抛异常", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/exists.json";
		await catalog.applyVersions([row(key, 1, H1)]);

		expect(await catalog.resolve(key, 2)).toBeNull();
		expect(await catalog.resolve("web.example.com/res/missing.json", null)).toBeNull();
		expect(await catalog.resolve("web.example.com/res/missing.json", 1)).toBeNull();
	});
});

describe("Catalog.applyVersions", () => {
	it("用相同哈希重复写入是幂等的", async () => {
		const catalog = newCatalog();
		const rows = [row("web.example.com/res/idempotent.json", 1, H1)];

		expect(await catalog.applyVersions(rows)).toEqual({ accepted: 1, conflicts: [] });
		expect(await catalog.applyVersions(rows)).toEqual({ accepted: 1, conflicts: [] });
		expect(await catalog.counts()).toEqual({ resources: 1, versions: 1 });
	});

	it("用不同哈希写入同一版本返回冲突，原值不变", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/conflict.json";
		await catalog.applyVersions([row(key, 1, H1)]);

		const result = await catalog.applyVersions([row(key, 1, H2, { size: 999, contentType: "image/png", collectedAt: "2030-01-01T00:00:00.000Z" })]);

		expect(result).toEqual({ accepted: 0, conflicts: [{ key, version: 1, existingHash: H1 }] });
		expect(await catalog.resolve(key, 1)).toEqual({
			version: 1,
			hash: H1,
			size: 101,
			contentType: "text/plain",
			collectedAt: "2026-01-01T00:00:00.000Z",
		});
		expect(await catalog.counts()).toEqual({ resources: 1, versions: 1 });
	});

	it("同一批次中冲突的行不影响其他行", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/mixed.json";
		const other = "web.example.com/res/mixed-other.json";
		await catalog.applyVersions([row(key, 1, H1)]);

		const result = await catalog.applyVersions([row(key, 1, H2), row(key, 2, H3), row(other, 1, H4)]);

		expect(result).toEqual({ accepted: 2, conflicts: [{ key, version: 1, existingHash: H1 }] });
		expect((await catalog.resolve(key, null))?.hash).toBe(H3);
		expect((await catalog.resolve(other, null))?.hash).toBe(H4);
	});

	it("同一批次内同一版本出现两个不同哈希时，后一个记为冲突", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/same-batch.json";

		const result = await catalog.applyVersions([row(key, 1, H1), row(key, 1, H2)]);

		expect(result).toEqual({ accepted: 1, conflicts: [{ key, version: 1, existingHash: H1 }] });
		expect((await catalog.resolve(key, 1))?.hash).toBe(H1);
	});

	it("latest 不会被较小的版本覆盖", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/latest.json";
		await catalog.applyVersions([row(key, 3, H3)]);
		await catalog.applyVersions([row(key, 1, H1), row(key, 2, H2)]);

		expect((await catalog.resolve(key, null))?.version).toBe(3);
		const info = await catalog.info(key);
		expect(info?.latest).toBe(3);
		expect(info?.versions.map((v) => v.version)).toEqual([1, 2, 3]);
	});
});

describe("Catalog.info", () => {
	it("返回按版本升序排列的全部版本；不存在时返回 null", async () => {
		const catalog = newCatalog();
		const key = "web.example.com/res/info.json";
		await catalog.applyVersions([row(key, 2, H2), row(key, 1, H1)]);

		expect(await catalog.info(key)).toEqual({
			key,
			latest: 2,
			versions: [
				{ version: 1, hash: H1, size: 101, contentType: "text/plain", collectedAt: "2026-01-01T00:00:00.000Z" },
				{ version: 2, hash: H2, size: 102, contentType: "text/plain", collectedAt: "2026-01-02T00:00:00.000Z" },
			],
		});
		expect(await catalog.info("web.example.com/res/missing.json")).toBeNull();
	});
});

describe("Catalog.listJson", () => {
	it("按 key 分组并排序（含中文 key），每组带 latest 与全部版本", async () => {
		const catalog = newCatalog();
		const chinese = "a.example.com/资源/三国杀.json";
		const ascii = "a.example.com/res/b.png";
		const script = "b.example.com/x.js";
		await catalog.applyVersions([
			row(script, 3, H3),
			row(chinese, 1, H1),
			row(script, 1, H1),
			row(ascii, 1, H2),
			row(chinese, 2, H2),
			row(script, 2, H2),
		]);

		const list = JSON.parse(await catalog.listJson());

		expect(Number.isNaN(Date.parse(list.generatedAt))).toBe(false);
		expect(list.count).toBe(3);
		// SQLite 按 UTF-8 字节序排序：ASCII 在前，中文在其后
		expect(list.resources).toEqual([
			{ key: ascii, latest: 1, versions: [{ version: 1, collectedAt: "2026-01-01T00:00:00.000Z" }] },
			{
				key: chinese,
				latest: 2,
				versions: [
					{ version: 1, collectedAt: "2026-01-01T00:00:00.000Z" },
					{ version: 2, collectedAt: "2026-01-02T00:00:00.000Z" },
				],
			},
			{
				key: script,
				latest: 3,
				versions: [
					{ version: 1, collectedAt: "2026-01-01T00:00:00.000Z" },
					{ version: 2, collectedAt: "2026-01-02T00:00:00.000Z" },
					{ version: 3, collectedAt: "2026-01-03T00:00:00.000Z" },
				],
			},
		]);
	});

	it("没有任何数据时返回空列表", async () => {
		const list = JSON.parse(await newCatalog().listJson());

		expect(list.count).toBe(0);
		expect(list.resources).toEqual([]);
	});
});

describe("Catalog.exportJson", () => {
	const k1 = "a.example.com/res/one.json";
	const k2 = "a.example.com/资源/二.json";
	const k3 = "b.example.com/three.js";

	async function seeded() {
		const catalog = newCatalog();
		await catalog.applyVersions([
			row(k3, 2, H2),
			row(k1, 1, H1),
			row(k3, 1, H1),
			row(k2, 1, H3),
			row(k1, 2, H2),
			row(k3, 3, H3, { size: 5, contentType: "application/javascript" }),
		]);
		return catalog;
	}

	type Page = {
		rows: { key: string; version: number; hash: string; size: number; contentType: string; collectedAt: string }[];
		next: { key: string; version: number } | null;
	};

	async function page(catalog: Awaited<ReturnType<typeof seeded>>, afterKey: string, afterVersion: number, limit: number): Promise<Page> {
		return JSON.parse(await catalog.exportJson(afterKey, afterVersion, limit));
	}

	it("按 (key, version) 排序分页，next 为最后一行的游标", async () => {
		const catalog = await seeded();

		const first = await page(catalog, "", 0, 4);
		expect(first.rows.map((r) => [r.key, r.version])).toEqual([
			[k1, 1],
			[k1, 2],
			[k2, 1],
			[k3, 1],
		]);
		expect(first.next).toEqual({ key: k3, version: 1 });

		const second = await page(catalog, first.next!.key, first.next!.version, 4);
		expect(second.rows.map((r) => [r.key, r.version])).toEqual([
			[k3, 2],
			[k3, 3],
		]);
		expect(second.next).toBeNull();
	});

	it("行字段完整且为 camelCase", async () => {
		const catalog = await seeded();

		const last = await page(catalog, k3, 2, 10);

		expect(last).toEqual({
			rows: [{ key: k3, version: 3, hash: H3, size: 5, contentType: "application/javascript", collectedAt: "2026-01-03T00:00:00.000Z" }],
			next: null,
		});
	});

	it("恰好取满 limit 时 next 仍指向最后一行，下一页为空且 next 为 null", async () => {
		const catalog = await seeded();

		const first = await page(catalog, "", 0, 3);
		expect(first.rows).toHaveLength(3);
		expect(first.next).toEqual({ key: k2, version: 1 });

		const second = await page(catalog, k2, 1, 3);
		expect(second.rows.map((r) => [r.key, r.version])).toEqual([
			[k3, 1],
			[k3, 2],
			[k3, 3],
		]);
		expect(second.next).toEqual({ key: k3, version: 3 });

		const third = await page(catalog, k3, 3, 3);
		expect(third).toEqual({ rows: [], next: null });
	});

	it("游标落在同一 key 的中间版本时，只返回其后的版本", async () => {
		const catalog = await seeded();

		const result = await page(catalog, k1, 1, 100);

		expect(result.rows.map((r) => [r.key, r.version])).toEqual([
			[k1, 2],
			[k2, 1],
			[k3, 1],
			[k3, 2],
			[k3, 3],
		]);
		expect(result.next).toBeNull();
	});

	it("逐页翻完得到全部行且不重复", async () => {
		const catalog = await seeded();
		const seen: [string, number][] = [];

		let cursor: Page["next"] = { key: "", version: 0 };
		while (cursor !== null) {
			const result: Page = await page(catalog, cursor.key, cursor.version, 2);
			seen.push(...result.rows.map((r): [string, number] => [r.key, r.version]));
			cursor = result.next;
		}

		expect(seen).toEqual([
			[k1, 1],
			[k1, 2],
			[k2, 1],
			[k3, 1],
			[k3, 2],
			[k3, 3],
		]);
	});
});

describe("Catalog.counts", () => {
	it("统计资源数与版本数", async () => {
		const catalog = newCatalog();
		expect(await catalog.counts()).toEqual({ resources: 0, versions: 0 });

		await catalog.applyVersions([
			row("a.example.com/res/c1.json", 1, H1),
			row("a.example.com/res/c1.json", 2, H2),
			row("a.example.com/res/c2.json", 1, H3),
			row("a.example.com/资源/c3.json", 1, H4),
			row("a.example.com/资源/c3.json", 2, H1),
			row("a.example.com/资源/c3.json", 3, H2),
		]);

		expect(await catalog.counts()).toEqual({ resources: 3, versions: 6 });
	});
});
