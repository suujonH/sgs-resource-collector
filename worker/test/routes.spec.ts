import { env, exports } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import type { CatalogVersion } from "../src/catalog";
import { authorized } from "../src/internal";
import { etagMatches, parseRange, parseResourcePath } from "../src/resource";

// workers.dev 上不使用 Cache API，走无缓存路径，结果确定；只有缓存冒烟用自定义域名
const ORIGIN = "https://sgs-test.example.workers.dev";
const CACHE_ORIGIN = "https://sgs-test.example.com";
const TOKEN = "test-token";
const SECURITY_HEADERS = { "X-Content-Type-Options": "nosniff", "Content-Security-Policy": "sandbox" };

const encoder = new TextEncoder();

type InternalInit = Omit<RequestInit, "headers"> & { headers?: Record<string, string> };

function send(path: string, init?: RequestInit, origin = ORIGIN): Promise<Response> {
	return exports.default.fetch(origin + path, init);
}

function internal(path: string, init: InternalInit = {}): Promise<Response> {
	return send(path, { ...init, headers: { Authorization: `Bearer ${TOKEN}`, ...init.headers } });
}

function randomHash(): string {
	return Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, "0")).join("");
}

function uniqueKey(label: string): string {
	return `routes.test/${label}-${crypto.randomUUID()}/file.txt`;
}

// 每段单独编码，保留路径里的 /
function encodePath(key: string): string {
	return key.split("/").map(encodeURIComponent).join("/");
}

function resourcePath(key: string, version?: number): string {
	return `/resource/${version === undefined ? "" : `${version}/`}${encodePath(key)}`;
}

// contentType 缺省时不发送 Content-Type 头
async function uploadBlob(content: string | Uint8Array, contentType?: string): Promise<{ hash: string; size: number }> {
	const hash = randomHash();
	const body = typeof content === "string" ? encoder.encode(content) : content;
	const headers: Record<string, string> = contentType === undefined ? {} : { "Content-Type": contentType };
	const res = await internal(`/internal/blobs/${hash}`, { method: "PUT", headers, body });
	expect(res.status).toBe(200);
	expect(await res.json()).toEqual({ ok: true, size: body.byteLength });
	return { hash, size: body.byteLength };
}

function postSync(rows: unknown[]): Promise<Response> {
	return internal("/internal/sync", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ versions: rows }) });
}

// 上传内容并同步一个版本，返回写入目录的行
async function publish(key: string, version: number, content: string, contentType = "text/plain"): Promise<CatalogVersion> {
	const { hash, size } = await uploadBlob(content, contentType);
	const row = { key, version, hash, size, contentType, collectedAt: `2026-02-${String(version).padStart(2, "0")}T08:00:00.000Z` };
	const res = await postSync([row]);
	expect(res.status).toBe(200);
	expect(await res.json()).toEqual({ accepted: 1, rejected: [], conflicts: [] });
	return row;
}

describe("内部接口鉴权", () => {
	const protectedRequests: [string, string, RequestInit][] = [
		["PUT /internal/blobs/{hash}", `/internal/blobs/${randomHash()}`, { method: "PUT", body: "x" }],
		["POST /internal/sync", "/internal/sync", { method: "POST", body: JSON.stringify({ versions: [] }) }],
		["GET /internal/stats", "/internal/stats", {}],
		["GET /internal/export", "/internal/export", {}],
		["GET /internal/unknown", "/internal/unknown", {}],
		["OPTIONS /internal/stats", "/internal/stats", { method: "OPTIONS" }],
	];
	const badCredentials: [string, Record<string, string>][] = [
		["没有 Authorization", {}],
		["token 错误（长度相同）", { Authorization: "Bearer test-tokeX" }],
		["token 错误（长度不同）", { Authorization: "Bearer wrong" }],
		["缺少 Bearer 前缀", { Authorization: TOKEN }],
		["只有 Bearer", { Authorization: "Bearer" }],
		["Basic 方案", { Authorization: "Basic dGVzdC10b2tlbg==" }],
	];

	it("没有 token 或 token 错误 → 401，no-store，且不带 CORS", async () => {
		for (const [requestName, path, init] of protectedRequests) {
			for (const [credentialName, headers] of badCredentials) {
				const res = await send(path, { ...init, headers });
				const label = `${requestName} / ${credentialName}`;
				expect(res.status, label).toBe(401);
				expect(res.headers.get("Cache-Control"), label).toBe("no-store");
				expect(res.headers.get("Access-Control-Allow-Origin"), label).toBeNull();
				expect(await res.json(), label).toEqual({ error: "unauthorized" });
			}
		}
	});

	it("未授权的 PUT 不会写入 R2", async () => {
		const hash = randomHash();

		const res = await send(`/internal/blobs/${hash}`, { method: "PUT", body: "should not be stored" });

		expect(res.status).toBe(401);
		await res.text();
		expect(await env.BLOBS.head(`blobs/${hash}`)).toBeNull();
	});

	it("授权后的未知路径或方法 → 404", async () => {
		for (const [path, method] of [
			["/internal/unknown", "GET"],
			["/internal/stats", "DELETE"],
			["/internal/sync", "GET"],
			["/internal/export", "POST"],
			["/internal", "GET"],
		]) {
			const res = await internal(path, { method });
			expect(res.status, `${method} ${path}`).toBe(404);
			expect(res.headers.get("Cache-Control")).toBe("no-store");
			await res.text();
		}
	});

	it("authorized：SYNC_TOKEN 为空或缺失时一律拒绝", () => {
		const withAuth = (value: string) => new Request(`${ORIGIN}/internal/stats`, { headers: { Authorization: value } });

		// 没配置密钥时，模板字符串会得到 "Bearer undefined"，不能因此放行
		expect(authorized(withAuth("Bearer undefined"), {} as Env)).toBe(false);
		expect(authorized(withAuth("Bearer null"), { SYNC_TOKEN: null } as unknown as Env)).toBe(false);
		expect(authorized(withAuth("Bearer"), { SYNC_TOKEN: "" } as Env)).toBe(false);
		expect(authorized(new Request(`${ORIGIN}/internal/stats`), { SYNC_TOKEN: "" } as Env)).toBe(false);
		expect(authorized(new Request(`${ORIGIN}/internal/stats`), {} as Env)).toBe(false);
	});

	it("authorized：只接受与 SYNC_TOKEN 完全一致的 Bearer 凭据", () => {
		const testEnv = { SYNC_TOKEN: "secret-token" } as Env;
		const withAuth = (value: string) => new Request(`${ORIGIN}/internal/stats`, { headers: { Authorization: value } });

		expect(authorized(withAuth("Bearer secret-token"), testEnv)).toBe(true);
		expect(authorized(withAuth("Bearer secret-tokeN"), testEnv)).toBe(false);
		expect(authorized(withAuth("bearer secret-token"), testEnv)).toBe(false);
		expect(authorized(withAuth("Bearer secret-token-longer"), testEnv)).toBe(false);
		expect(authorized(withAuth("Bearer secret"), testEnv)).toBe(false);
	});
});

describe("PUT /internal/blobs/{hash}", () => {
	it("hash 不是 32 位小写十六进制 → 400", async () => {
		for (const hash of ["xyz", "0".repeat(31), "0".repeat(33), "A".repeat(32), "g".repeat(32), "0".repeat(32) + "/x"]) {
			const res = await internal(`/internal/blobs/${hash}`, { method: "PUT", body: "x" });
			expect(res.status, hash).toBe(400);
			expect(res.headers.get("Cache-Control")).toBe("no-store");
			await res.text();
		}
		const empty = await internal("/internal/blobs/", { method: "PUT", body: "x" });
		expect(empty.status).toBe(400);
		await empty.text();
	});

	it("没有 Content-Length → 411", async () => {
		const hash = randomHash();

		const res = await internal(`/internal/blobs/${hash}`, { method: "PUT" });

		expect(res.status).toBe(411);
		expect(res.headers.get("Cache-Control")).toBe("no-store");
		await res.text();
		expect(await env.BLOBS.head(`blobs/${hash}`)).toBeNull();
	});

	it("写入 blobs/{hash}，记录 Content-Type，缺省为 application/octet-stream", async () => {
		const typed = await uploadBlob("typed", "application/json");
		const untyped = await uploadBlob(new Uint8Array([1, 2, 3, 4]));

		expect((await env.BLOBS.head(`blobs/${typed.hash}`))?.httpMetadata?.contentType).toBe("application/json");
		const stored = await env.BLOBS.get(`blobs/${untyped.hash}`);
		expect(stored?.httpMetadata?.contentType).toBe("application/octet-stream");
		expect(stored?.size).toBe(4);
		expect(Array.from(new Uint8Array((await stored?.arrayBuffer()) ?? new ArrayBuffer(0)))).toEqual([1, 2, 3, 4]);
	});

	it("相同 hash 重复上传是幂等的；空内容也可以上传", async () => {
		const hash = randomHash();
		for (let i = 0; i < 2; i++) {
			const res = await internal(`/internal/blobs/${hash}`, { method: "PUT", body: "same" });
			expect(res.status).toBe(200);
			expect(await res.json()).toEqual({ ok: true, size: 4 });
		}

		const emptyHash = randomHash();
		const empty = await internal(`/internal/blobs/${emptyHash}`, { method: "PUT", body: new Uint8Array(0) });
		expect(empty.status).toBe(200);
		expect(await empty.json()).toEqual({ ok: true, size: 0 });
	});
});

describe("GET /resource", () => {
	it("同步 v1、v2 之后：按版本读取，无版本号读取最新版，并带安全头", async () => {
		const key = uniqueKey("versions");
		const v1 = await publish(key, 1, "version one", "application/json");
		const v2 = await publish(key, 2, "version two (longer)", "text/html; charset=utf-8");

		const first = await send(resourcePath(key, 1));
		expect(first.status).toBe(200);
		expect(await first.text()).toBe("version one");
		expect(first.headers.get("X-Resource-Version")).toBe("1");
		expect(first.headers.get("ETag")).toBe(`"${v1.hash}"`);
		expect(first.headers.get("Content-Type")).toBe("application/json");
		expect(first.headers.get("Cache-Control")).toBe("public, max-age=31536000, immutable");

		for (const path of [resourcePath(key, 2), resourcePath(key)]) {
			const res = await send(path);
			expect(res.status, path).toBe(200);
			expect(await res.text(), path).toBe("version two (longer)");
			expect(res.headers.get("X-Resource-Version"), path).toBe("2");
			expect(res.headers.get("ETag"), path).toBe(`"${v2.hash}"`);
			expect(res.headers.get("Content-Type"), path).toBe("text/html; charset=utf-8");
			expect(res.headers.get("Content-Length"), path).toBe(String(v2.size));
			expect(res.headers.get("Accept-Ranges"), path).toBe("bytes");
			for (const [name, value] of Object.entries(SECURITY_HEADERS)) {
				expect(res.headers.get(name), `${path} ${name}`).toBe(value);
			}
		}
		expect((await send(resourcePath(key, 2))).headers.get("Cache-Control")).toBe("public, max-age=31536000, immutable");
		expect((await send(resourcePath(key))).headers.get("Cache-Control")).toBe("public, max-age=60");
	});

	it("带 CORS 头，并公开跨域读取需要的响应头", async () => {
		const key = uniqueKey("cors");
		await publish(key, 1, "cors");

		const res = await send(resourcePath(key));

		expect(res.headers.get("Access-Control-Allow-Origin")).toBe("*");
		expect(res.headers.get("Access-Control-Expose-Headers")).toBe("ETag, X-Resource-Version, Content-Range, Content-Length");
		await res.text();
	});

	it("最新版地址随新版本推进，带版本号的地址保持不变", async () => {
		const key = uniqueKey("advance");
		await publish(key, 1, "first");
		const before = await send(resourcePath(key));
		expect(await before.text()).toBe("first");

		await publish(key, 2, "second");

		const latest = await send(resourcePath(key));
		expect(await latest.text()).toBe("second");
		expect(latest.headers.get("X-Resource-Version")).toBe("2");
		expect(await (await send(resourcePath(key, 1))).text()).toBe("first");
	});

	it("不存在的 key 或版本 → 404，带 Cache-Control: no-store", async () => {
		const key = uniqueKey("missing");
		await publish(key, 1, "only v1");

		for (const path of [resourcePath(uniqueKey("never-published")), resourcePath(uniqueKey("never-published"), 1), resourcePath(key, 2), resourcePath(key, 999)]) {
			const res = await send(path);
			expect(res.status, path).toBe(404);
			expect(res.headers.get("Cache-Control"), path).toBe("no-store");
			expect(await res.json(), path).toEqual({ error: "not_found" });
		}
	});

	it("路径不合法 → 400，带 Cache-Control: no-store", async () => {
		for (const path of ["/resource/", "/resource/nokey", "/resource/5", "/resource/5/nokey", "/resource/2/", "/resource/web.example.com/%E0%A4%A"]) {
			const res = await send(path);
			expect(res.status, path).toBe(400);
			expect(res.headers.get("Cache-Control"), path).toBe("no-store");
			await res.text();
		}
	});

	it("记录存在但 R2 对象缺失 → 502 blob_missing", async () => {
		const key = uniqueKey("blob-missing");
		const row = { key, version: 1, hash: randomHash(), size: 5, contentType: "text/plain", collectedAt: "2026-02-01T00:00:00.000Z" };
		expect(await (await postSync([row])).json()).toEqual({ accepted: 1, rejected: [], conflicts: [] });

		for (const [origin, init] of [
			[ORIGIN, undefined],
			[ORIGIN, { headers: { Range: "bytes=0-1" } }],
			[CACHE_ORIGIN, undefined],
		] as [string, RequestInit | undefined][]) {
			const res = await send(resourcePath(key), init, origin);
			expect(res.status, origin).toBe(502);
			expect(res.headers.get("Cache-Control")).toBe("no-store");
			expect(await res.json()).toEqual({ error: "blob_missing" });
		}

		// HEAD 只查目录，不读取 R2
		const head = await send(resourcePath(key), { method: "HEAD" });
		expect(head.status).toBe(200);
		expect(head.headers.get("X-Resource-Version")).toBe("1");
		expect(await head.text()).toBe("");
	});

	describe("Range", () => {
		async function ranged(range: string, key: string, version?: number) {
			return send(resourcePath(key, version), { headers: { Range: range } });
		}

		it("bytes=0-3 → 206，Content-Range 正确", async () => {
			const key = uniqueKey("range-head");
			await publish(key, 1, "0123456789");

			const res = await ranged("bytes=0-3", key);

			expect(res.status).toBe(206);
			expect(await res.text()).toBe("0123");
			expect(res.headers.get("Content-Range")).toBe("bytes 0-3/10");
			expect(res.headers.get("Content-Length")).toBe("4");
			expect(res.headers.get("ETag")).not.toBeNull();
			expect(res.headers.get("X-Resource-Version")).toBe("1");
		});

		it("bytes=-2 → 最后两个字节", async () => {
			const key = uniqueKey("range-suffix");
			await publish(key, 1, "0123456789");

			const res = await ranged("bytes=-2", key);

			expect(res.status).toBe(206);
			expect(await res.text()).toBe("89");
			expect(res.headers.get("Content-Range")).toBe("bytes 8-9/10");
			expect(res.headers.get("Content-Length")).toBe("2");
		});

		it("开放区间与越过末尾的区间被截断到文件末尾", async () => {
			const key = uniqueKey("range-open");
			await publish(key, 1, "0123456789");

			const open = await ranged("bytes=4-", key, 1);
			expect(open.status).toBe(206);
			expect(await open.text()).toBe("456789");
			expect(open.headers.get("Content-Range")).toBe("bytes 4-9/10");

			const clamped = await ranged("bytes=5-99", key);
			expect(clamped.status).toBe(206);
			expect(await clamped.text()).toBe("56789");
			expect(clamped.headers.get("Content-Range")).toBe("bytes 5-9/10");

			const whole = await ranged("bytes=-99", key);
			expect(whole.status).toBe(206);
			expect(await whole.text()).toBe("0123456789");
			expect(whole.headers.get("Content-Range")).toBe("bytes 0-9/10");
		});

		it("越界 → 416，Content-Range: bytes */size，no-store", async () => {
			const key = uniqueKey("range-416");
			await publish(key, 1, "0123456789");

			for (const range of ["bytes=10-", "bytes=10-20", "bytes=100-", "bytes=-0"]) {
				const res = await ranged(range, key);
				expect(res.status, range).toBe(416);
				expect(res.headers.get("Content-Range"), range).toBe("bytes */10");
				expect(res.headers.get("Cache-Control"), range).toBe("no-store");
				expect(await res.text(), range).toBe("");
			}
		});

		it("空文件上任何 Range 都是 416", async () => {
			const key = uniqueKey("range-empty");
			await publish(key, 1, "");

			for (const range of ["bytes=0-", "bytes=0-0", "bytes=-1"]) {
				const res = await ranged(range, key);
				expect(res.status, range).toBe(416);
				expect(res.headers.get("Content-Range"), range).toBe("bytes */0");
				await res.text();
			}
		});

		it("多段、非 bytes 单位或格式错误的 Range 被忽略，返回完整内容", async () => {
			const key = uniqueKey("range-ignored");
			await publish(key, 1, "0123456789");

			for (const range of ["bytes=0-1,4-5", "items=0-3", "bytes=5-2", "bytes=abc", "bytes=", "bytes=-", "bytes=1-2-3"]) {
				const res = await ranged(range, key);
				expect(res.status, range).toBe(200);
				expect(await res.text(), range).toBe("0123456789");
				expect(res.headers.get("Content-Range"), range).toBeNull();
			}
		});
	});

	describe("If-None-Match", () => {
		it('"hash" 与 W/"hash" → 304，无 body，仍带 ETag 与缓存头', async () => {
			const key = uniqueKey("etag");
			const row = await publish(key, 1, "etag content");

			for (const header of [`"${row.hash}"`, `W/"${row.hash}"`, `"other", "${row.hash}"`, `"other",W/"${row.hash}"`, "*"]) {
				const res = await send(resourcePath(key), { headers: { "If-None-Match": header } });
				expect(res.status, header).toBe(304);
				expect(await res.text(), header).toBe("");
				expect(res.headers.get("ETag"), header).toBe(`"${row.hash}"`);
				expect(res.headers.get("Cache-Control"), header).toBe("public, max-age=60");
				expect(res.headers.get("X-Resource-Version"), header).toBe("1");
			}
		});

		it("ETag 不匹配 → 200 完整内容", async () => {
			const key = uniqueKey("etag-miss");
			const row = await publish(key, 1, "etag content");

			for (const header of [`"${"0".repeat(32)}"`, row.hash, `W/${row.hash}`, ""]) {
				const res = await send(resourcePath(key), { headers: { "If-None-Match": header } });
				expect(res.status, header).toBe(200);
				expect(await res.text(), header).toBe("etag content");
			}
		});

		it("版本号地址上同样生效", async () => {
			const key = uniqueKey("etag-versioned");
			const v1 = await publish(key, 1, "one");
			const v2 = await publish(key, 2, "two");

			const same = await send(resourcePath(key, 1), { headers: { "If-None-Match": `"${v1.hash}"` } });
			expect(same.status).toBe(304);
			expect(same.headers.get("Cache-Control")).toBe("public, max-age=31536000, immutable");

			const stale = await send(resourcePath(key), { headers: { "If-None-Match": `"${v1.hash}"` } });
			expect(stale.status).toBe(200);
			expect(await stale.text()).toBe("two");
			expect(stale.headers.get("ETag")).toBe(`"${v2.hash}"`);
		});
	});

	it("HEAD → 200，带响应头，没有 body", async () => {
		const key = uniqueKey("head");
		const row = await publish(key, 1, "head content", "text/plain");

		const res = await send(resourcePath(key), { method: "HEAD" });

		expect(res.status).toBe(200);
		expect(await res.text()).toBe("");
		expect(res.headers.get("ETag")).toBe(`"${row.hash}"`);
		expect(res.headers.get("X-Resource-Version")).toBe("1");
		expect(res.headers.get("Content-Type")).toBe("text/plain");
		expect(res.headers.get("Cache-Control")).toBe("public, max-age=60");
		// 运行时可能移除 HEAD 响应上手动设置的 Content-Length，不算错误
		const length = res.headers.get("Content-Length");
		if (length !== null) {
			expect(length).toBe(String(row.size));
		}

		const missing = await send(resourcePath(uniqueKey("head-missing")), { method: "HEAD" });
		expect(missing.status).toBe(404);
		expect(missing.headers.get("Cache-Control")).toBe("no-store");
	});

	it("中文路径经 URL 编码后可以访问", async () => {
		const key = `routes.test/资源-${crypto.randomUUID()}/三国杀 图片.json`;
		await publish(key, 1, '{"名称":"杀"}', "application/json");
		await publish(key, 2, '{"名称":"闪"}', "application/json");
		const encoded = encodePath(key);
		expect(encoded).toContain("%E8%B5%84%E6%BA%90");

		const latest = await send(`/resource/${encoded}`);
		expect(latest.status).toBe(200);
		expect(await latest.text()).toBe('{"名称":"闪"}');

		const first = await send(`/resource/1/${encoded}`);
		expect(first.status).toBe(200);
		expect(await first.text()).toBe('{"名称":"杀"}');

		// 整个 key 作为一段编码（/ 也被编码）同样可以访问
		const wholeEncoded = await send(`/resource/${encodeURIComponent(key)}`);
		expect(wholeEncoded.status).toBe(200);
		expect(await wholeEncoded.text()).toBe('{"名称":"闪"}');

		for (const path of [`/api/info/${encoded}`, `/api/info/${encodeURIComponent(key)}`]) {
			const info = await send(path);
			expect(info.status, path).toBe(200);
			expect(((await info.json()) as { key: string }).key, path).toBe(key);
		}
	});
});

describe("预检与其余路径", () => {
	it("OPTIONS 返回 204 与 CORS 预检头；内部接口不处理 OPTIONS", async () => {
		for (const path of ["/resource/web.example.com/a.png", "/api/list", "/anything"]) {
			const res = await send(path, { method: "OPTIONS" });
			expect(res.status, path).toBe(204);
			expect(res.headers.get("Access-Control-Allow-Origin"), path).toBe("*");
			expect(res.headers.get("Access-Control-Allow-Methods"), path).toBe("GET, HEAD, OPTIONS");
			expect(res.headers.get("Access-Control-Allow-Headers"), path).toBe("Range, If-None-Match");
			expect(res.headers.get("Access-Control-Max-Age"), path).toBe("86400");
			expect(await res.text(), path).toBe("");
		}
	});

	it("其余路径与方法 → 404，带 no-store", async () => {
		const cases: [string, string][] = [
			["GET", "/"],
			["GET", "/resource"],
			["GET", "/api/other"],
			["GET", "/api/list/extra"],
			["POST", "/api/list"],
			["HEAD", "/api/list"],
			["POST", "/resource/web.example.com/a.png"],
			["PUT", "/resource/web.example.com/a.png"],
			["POST", "/api/info/web.example.com/a.png"],
		];
		for (const [method, path] of cases) {
			const res = await send(path, { method });
			expect(res.status, `${method} ${path}`).toBe(404);
			expect(res.headers.get("Cache-Control"), `${method} ${path}`).toBe("no-store");
			await res.text();
		}
	});
});

describe("/api", () => {
	it("/api/info/{key} 返回全部版本的字段", async () => {
		const key = uniqueKey("info");
		const v1 = await publish(key, 1, "info one", "text/plain");
		const v2 = await publish(key, 2, '{"two":2}', "application/json");

		const res = await send(`/api/info/${encodePath(key)}`);

		expect(res.status).toBe(200);
		expect(res.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
		expect(res.headers.get("Cache-Control")).toBe("public, max-age=60");
		expect(res.headers.get("Access-Control-Allow-Origin")).toBe("*");
		expect(await res.json()).toEqual({
			key,
			latest: 2,
			versions: [
				{ version: 1, hash: v1.hash, size: v1.size, contentType: "text/plain", collectedAt: v1.collectedAt },
				{ version: 2, hash: v2.hash, size: v2.size, contentType: "application/json", collectedAt: v2.collectedAt },
			],
		});
	});

	it("/api/info：不存在 → 404 no-store；key 不合法 → 400", async () => {
		const missing = await send(`/api/info/${encodePath(uniqueKey("info-missing"))}`);
		expect(missing.status).toBe(404);
		expect(missing.headers.get("Cache-Control")).toBe("no-store");
		await missing.text();

		for (const path of ["/api/info/", "/api/info/nokey", "/api/info/web.example.com/%E0%A4%A"]) {
			const res = await send(path);
			expect(res.status, path).toBe(400);
			expect(res.headers.get("Cache-Control"), path).toBe("no-store");
			await res.text();
		}
	});

	it("/api/list 返回分组后的版本列表", async () => {
		const first = uniqueKey("list-a");
		const second = `routes.test/列表-${crypto.randomUUID()}/b.txt`;
		const a1 = await publish(first, 1, "a1");
		const a2 = await publish(first, 2, "a2");
		const b1 = await publish(second, 1, "b1");

		const res = await send("/api/list");

		expect(res.status).toBe(200);
		expect(res.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
		expect(res.headers.get("Cache-Control")).toBe("public, max-age=60");
		expect(res.headers.get("Access-Control-Allow-Origin")).toBe("*");
		const list = (await res.json()) as {
			generatedAt: string;
			count: number;
			resources: { key: string; latest: number; versions: { version: number; collectedAt: string }[] }[];
		};
		expect(Number.isNaN(Date.parse(list.generatedAt))).toBe(false);
		expect(list.count).toBe(list.resources.length);
		expect(list.resources.find((r) => r.key === first)).toEqual({
			key: first,
			latest: 2,
			versions: [
				{ version: 1, collectedAt: a1.collectedAt },
				{ version: 2, collectedAt: a2.collectedAt },
			],
		});
		expect(list.resources.find((r) => r.key === second)).toEqual({ key: second, latest: 1, versions: [{ version: 1, collectedAt: b1.collectedAt }] });
	});
});

describe("POST /internal/sync", () => {
	function good(key: string, version: number, hash: string) {
		return { key, version, hash, size: 3, contentType: "text/plain", collectedAt: "2026-03-01T00:00:00.000Z" };
	}

	it("整批不合法（不是 JSON、缺少 versions、行数不在 1–1000）→ 400", async () => {
		const bodies: [string, string][] = [
			["不是 JSON", "not json"],
			["JSON 但不是对象", "[]"],
			["null", "null"],
			["缺少 versions", "{}"],
			["versions 不是数组", JSON.stringify({ versions: "x" })],
			["versions 为空", JSON.stringify({ versions: [] })],
			["超过 1000 行", JSON.stringify({ versions: Array.from({ length: 1001 }, () => ({})) })],
		];
		for (const [name, body] of bodies) {
			const res = await internal("/internal/sync", { method: "POST", headers: { "Content-Type": "application/json" }, body });
			expect(res.status, name).toBe(400);
			expect(res.headers.get("Cache-Control"), name).toBe("no-store");
			await res.text();
		}
	});

	it("混有非法行和冲突行：合法行正常写入，非法行进 rejected，冲突行进 conflicts", async () => {
		const existing = await publish(uniqueKey("sync-existing"), 1, "existing");
		const fresh = uniqueKey("sync-fresh");
		const invalid = uniqueKey("sync-invalid");
		const longKey = `routes.test/${crypto.randomUUID()}/`.padEnd(2048, "k");
		const hashFresh = randomHash();
		const hashOther = randomHash();
		const hashLong = randomHash();
		const hashJunk = randomHash();

		const rows: unknown[] = [
			good(fresh, 1, hashFresh), // 0 合法
			{ ...good(invalid, 1, hashJunk), hash: "A".repeat(32) }, // 1 哈希含大写
			good(existing.key, 1, hashOther), // 2 与已有版本冲突
			good(invalid, 0, hashJunk), // 3 版本号为 0
			good(existing.key, 1, existing.hash), // 4 合法：重复推送相同内容
			"oops", // 5 不是对象
			good("nokey", 1, hashJunk), // 6 key 不含 /
			{ ...good(invalid, 1, hashJunk), size: -1 }, // 7 size 为负
			{ ...good(invalid, 1, hashJunk), contentType: "" }, // 8 contentType 为空
			{ ...good(invalid, 1, hashJunk), collectedAt: "x".repeat(41) }, // 9 collectedAt 超长
			good(invalid, 2147483648, hashJunk), // 10 版本号超过上限
			good(invalid, 1.5, hashJunk), // 11 版本号不是整数
			good("a/".padEnd(2049, "b"), 1, hashJunk), // 12 key 超长
			{ ...good(invalid, 1, hashJunk), size: 2 ** 53 }, // 13 size 不是安全整数
			null, // 14 null
			{ ...good(invalid, 1, hashJunk), version: "1" }, // 15 版本号是字符串
			good(longKey, 1, hashLong), // 16 合法：key 恰好 2048 字符
			good("a/", 1, hashJunk), // 17 key 太短
			{ ...good(invalid, 1, hashJunk), contentType: "t".repeat(201) }, // 18 contentType 超长
			{ ...good(invalid, 1, hashJunk), size: 1.5 }, // 19 size 不是整数
			{ ...good(invalid, 1, hashJunk), hash: undefined }, // 20 缺少 hash
		];

		const res = await postSync(rows);

		expect(res.status).toBe(200);
		expect(await res.json()).toEqual({
			accepted: 3,
			rejected: [
				{ index: 1, reason: "invalid_hash" },
				{ index: 3, reason: "invalid_version" },
				{ index: 5, reason: "invalid_row" },
				{ index: 6, reason: "invalid_key" },
				{ index: 7, reason: "invalid_size" },
				{ index: 8, reason: "invalid_content_type" },
				{ index: 9, reason: "invalid_collected_at" },
				{ index: 10, reason: "invalid_version" },
				{ index: 11, reason: "invalid_version" },
				{ index: 12, reason: "invalid_key" },
				{ index: 13, reason: "invalid_size" },
				{ index: 14, reason: "invalid_row" },
				{ index: 15, reason: "invalid_version" },
				{ index: 17, reason: "invalid_key" },
				{ index: 18, reason: "invalid_content_type" },
				{ index: 19, reason: "invalid_size" },
				{ index: 20, reason: "invalid_hash" },
			],
			conflicts: [{ key: existing.key, version: 1, existingHash: existing.hash }],
		});

		// 合法行写入，冲突行不覆盖，非法行不写入
		const freshInfo = await send(`/api/info/${encodePath(fresh)}`);
		expect(((await freshInfo.json()) as { versions: { hash: string }[] }).versions.map((v) => v.hash)).toEqual([hashFresh]);
		const existingInfo = await send(`/api/info/${encodePath(existing.key)}`);
		expect(((await existingInfo.json()) as { versions: { hash: string }[] }).versions.map((v) => v.hash)).toEqual([existing.hash]);
		const longInfo = await send(`/api/info/${encodeURIComponent(longKey)}`);
		expect(longInfo.status).toBe(200);
		await longInfo.text();
		const invalidInfo = await send(`/api/info/${encodePath(invalid)}`);
		expect(invalidInfo.status).toBe(404);
		await invalidInfo.text();
	});

	it("所有行都不合法时也返回 200，accepted 为 0", async () => {
		const res = await postSync([{ key: "nokey" }, 42]);

		expect(res.status).toBe(200);
		expect(await res.json()).toEqual({
			accepted: 0,
			rejected: [
				{ index: 0, reason: "invalid_key" },
				{ index: 1, reason: "invalid_row" },
			],
			conflicts: [],
		});
	});

	it("重复推送相同内容是幂等的，版本数不变", async () => {
		const row = await publish(uniqueKey("sync-idempotent"), 1, "idempotent");
		const before = (await (await internal("/internal/stats")).json()) as { resources: number; versions: number };

		const again = await postSync([row, row]);

		expect(await again.json()).toEqual({ accepted: 2, rejected: [], conflicts: [] });
		expect(await (await internal("/internal/stats")).json()).toEqual(before);
	});
});

describe("GET /internal/stats 与 /internal/export", () => {
	type ExportRow = { key: string; version: number; hash: string; size: number; contentType: string; collectedAt: string };
	type ExportPage = { rows: ExportRow[]; next: { key: string; version: number } | null };

	async function exportPage(afterKey: string, afterVersion: number, limit: number): Promise<ExportPage> {
		const query = new URLSearchParams({ afterKey, afterVersion: String(afterVersion), limit: String(limit) });
		const res = await internal(`/internal/export?${query}`);
		expect(res.status).toBe(200);
		expect(res.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
		return (await res.json()) as ExportPage;
	}

	it("stats 返回资源数与版本数", async () => {
		const before = (await (await internal("/internal/stats")).json()) as { resources: number; versions: number };

		await publish(uniqueKey("stats-a"), 1, "a1");
		const second = uniqueKey("stats-b");
		await publish(second, 1, "b1");
		await publish(second, 2, "b2");

		const res = await internal("/internal/stats");
		expect(res.status).toBe(200);
		expect(await res.json()).toEqual({ resources: before.resources + 2, versions: before.versions + 3 });
	});

	it("export 能分页：游标连续、不重复、不遗漏", async () => {
		const prefix = `routes.test/导出-${crypto.randomUUID()}`;
		const keys = [`${prefix}/b.txt`, `${prefix}/资源.txt`, `${prefix}/a.txt`];
		const published: CatalogVersion[] = [];
		published.push(await publish(keys[0], 1, "b1"));
		published.push(await publish(keys[0], 2, "b2"));
		published.push(await publish(keys[1], 1, "r1"));
		published.push(await publish(keys[2], 1, "a1"));

		// prefix 本身排在所有 prefix/... 之前；从它开始翻页，直到收齐本用例的行（其后可能还有其他用例的行）
		const collected: ExportRow[] = [];
		let cursor: NonNullable<ExportPage["next"]> = { key: prefix, version: 0 };
		for (let pages = 0; pages < 20; pages++) {
			const page = await exportPage(cursor.key, cursor.version, 3);
			expect(page.rows.length).toBeLessThanOrEqual(3);
			if (page.next !== null) {
				expect(page.rows).toHaveLength(3);
				expect(page.next).toEqual({ key: page.rows[2].key, version: page.rows[2].version });
			}
			collected.push(...page.rows);
			if (page.next === null || collected.filter((r) => r.key.startsWith(prefix)).length === published.length) {
				break;
			}
			cursor = page.next;
		}

		for (let i = 1; i < collected.length; i++) {
			const prev = collected[i - 1];
			const cur = collected[i];
			expect(prev.key < cur.key || (prev.key === cur.key && prev.version < cur.version), `${prev.key}@${prev.version} 应排在 ${cur.key}@${cur.version} 之前`).toBe(true);
		}
		const mine = collected.filter((r) => r.key.startsWith(prefix));
		expect(mine.map((r) => [r.key, r.version])).toEqual([
			[keys[2], 1],
			[keys[0], 1],
			[keys[0], 2],
			[keys[1], 1],
		]);
		const byKey = (key: string, version: number) => published.find((p) => p.key === key && p.version === version)!;
		for (const row of mine) {
			expect(row).toEqual(byKey(row.key, row.version));
		}
	});

	it("export 的默认参数与 limit 限制", async () => {
		await publish(uniqueKey("export-defaults"), 1, "x");
		await publish(uniqueKey("export-defaults"), 1, "y");

		const defaults = await internal("/internal/export");
		expect(defaults.status).toBe(200);
		const page = (await defaults.json()) as ExportPage;
		expect(page.rows.length).toBeGreaterThanOrEqual(2);
		expect(page.rows.length).toBeLessThanOrEqual(1000);
		expect(page.rows[0]).toHaveProperty("contentType");

		// limit 小于 1 时按 1 处理
		for (const limit of ["0", "-5"]) {
			const res = await internal(`/internal/export?limit=${limit}`);
			expect(res.status, limit).toBe(200);
			const clamped = (await res.json()) as ExportPage;
			expect(clamped.rows, limit).toHaveLength(1);
			expect(clamped.next, limit).toEqual({ key: clamped.rows[0].key, version: clamped.rows[0].version });
		}
	});

	it("export 的 limit 最大为 2000；sync 一次可以写入 1000 行", async () => {
		const prefix = `routes.test/批量-${crypto.randomUUID()}`;
		const hash = randomHash();
		const rows = Array.from({ length: 2001 }, (_, i) => ({
			key: `${prefix}/${String(i).padStart(4, "0")}`,
			version: 1,
			hash,
			size: 1,
			contentType: "text/plain",
			collectedAt: "2026-04-01T00:00:00.000Z",
		}));
		for (let start = 0; start < rows.length; start += 1000) {
			const chunk = rows.slice(start, start + 1000);
			const res = await postSync(chunk);
			expect(res.status).toBe(200);
			expect(await res.json()).toEqual({ accepted: chunk.length, rejected: [], conflicts: [] });
		}

		const page = await exportPage(prefix, 0, 100000);

		expect(page.rows).toHaveLength(2000);
		expect(page.rows[0].key).toBe(`${prefix}/0000`);
		expect(page.rows[1999].key).toBe(`${prefix}/1999`);
		expect(page.next).toEqual({ key: `${prefix}/1999`, version: 1 });
	});

	it("export 的参数不是整数 → 400", async () => {
		for (const query of ["limit=abc", "limit=1.5", "afterVersion=abc", "afterVersion=-1", "afterVersion=1.5"]) {
			const res = await internal(`/internal/export?${query}`);
			expect(res.status, query).toBe(400);
			expect(res.headers.get("Cache-Control"), query).toBe("no-store");
			await res.text();
		}
	});
});

describe("缓存冒烟（自定义域名）", () => {
	it("连续两次完整 GET 都返回 200 且内容正确", async () => {
		const key = uniqueKey("cache-smoke");
		const row = await publish(key, 1, "cached content", "text/plain");

		for (let i = 0; i < 2; i++) {
			const res = await send(resourcePath(key), undefined, CACHE_ORIGIN);
			expect(res.status, `第 ${i + 1} 次`).toBe(200);
			expect(await res.text(), `第 ${i + 1} 次`).toBe("cached content");
			expect(res.headers.get("ETag"), `第 ${i + 1} 次`).toBe(`"${row.hash}"`);
			expect(res.headers.get("X-Resource-Version"), `第 ${i + 1} 次`).toBe("1");
			expect(res.headers.get("Content-Type"), `第 ${i + 1} 次`).toBe("text/plain");
		}
	});

	it("带版本号的地址与不存在的资源在自定义域名上同样正确", async () => {
		const key = uniqueKey("cache-versioned");
		await publish(key, 1, "v1 cached");

		const versioned = await send(resourcePath(key, 1), undefined, CACHE_ORIGIN);
		expect(versioned.status).toBe(200);
		expect(await versioned.text()).toBe("v1 cached");
		expect(versioned.headers.get("Cache-Control")).toBe("public, max-age=31536000, immutable");

		const missing = await send(resourcePath(uniqueKey("cache-missing")), undefined, CACHE_ORIGIN);
		expect(missing.status).toBe(404);
		expect(missing.headers.get("Cache-Control")).toBe("no-store");
		await missing.text();
	});

	it("/api/list 与 /api/info 在自定义域名上连续两次请求结果一致", async () => {
		const key = uniqueKey("cache-api");
		const row = await publish(key, 1, "api cached");
		// 查询串不同缓存键就不同，避免命中其他用例留下的缓存
		const listPath = `/api/list?smoke=${crypto.randomUUID()}`;

		for (let i = 0; i < 2; i++) {
			const info = await send(`/api/info/${encodePath(key)}`, undefined, CACHE_ORIGIN);
			expect(info.status, `info 第 ${i + 1} 次`).toBe(200);
			expect(await info.json()).toEqual({
				key,
				latest: 1,
				versions: [{ version: 1, hash: row.hash, size: row.size, contentType: "text/plain", collectedAt: row.collectedAt }],
			});

			const list = await send(listPath, undefined, CACHE_ORIGIN);
			expect(list.status, `list 第 ${i + 1} 次`).toBe(200);
			const body = (await list.json()) as { resources: { key: string }[] };
			expect(body.resources.some((r) => r.key === key), `list 第 ${i + 1} 次`).toBe(true);
		}
	});
});

describe("资源路径、Range 与 ETag 解析", () => {
	it.each<[string, { key: string; version: number | null } | null]>([
		["", null],
		["web.example.com/a/b.png", { key: "web.example.com/a/b.png", version: null }],
		["1/web.example.com/a/b.png", { key: "web.example.com/a/b.png", version: 1 }],
		["12/web.example.com/a/b.png", { key: "web.example.com/a/b.png", version: 12 }],
		["999999999/web.example.com/a", { key: "web.example.com/a", version: 999999999 }],
		["1000000000/web.example.com/a", { key: "1000000000/web.example.com/a", version: null }],
		["0/web.example.com/a", { key: "0/web.example.com/a", version: null }],
		["01/web.example.com/a", { key: "01/web.example.com/a", version: null }],
		["1.2.3.4/a/b", { key: "1.2.3.4/a/b", version: null }],
		["5", null],
		["5/abc", null],
		["2/", null],
		["nokey", null],
		["web.example.com/%E4%B8%AD%E6%96%87.json", { key: "web.example.com/中文.json", version: null }],
		["3/web.example.com/%E4%B8%AD.json", { key: "web.example.com/中.json", version: 3 }],
		["web.example.com%2Fa.png", { key: "web.example.com/a.png", version: null }],
		["web.example.com/a%20b.png", { key: "web.example.com/a b.png", version: null }],
		["web.example.com/%E0%A4%A", null],
		["web.example.com//a", { key: "web.example.com//a", version: null }],
	])("parseResourcePath(%j)", (rest, expected) => {
		expect(parseResourcePath(rest)).toEqual(expected);
	});

	it.each<[string, number, { offset: number; length: number } | "unsatisfiable" | null]>([
		["bytes=0-3", 10, { offset: 0, length: 4 }],
		["bytes=0-0", 10, { offset: 0, length: 1 }],
		["bytes=9-9", 10, { offset: 9, length: 1 }],
		["bytes=5-99", 10, { offset: 5, length: 5 }],
		["bytes=2-", 10, { offset: 2, length: 8 }],
		["bytes=0-", 10, { offset: 0, length: 10 }],
		["bytes=-3", 10, { offset: 7, length: 3 }],
		["bytes=-99", 10, { offset: 0, length: 10 }],
		["bytes=10-", 10, "unsatisfiable"],
		["bytes=10-12", 10, "unsatisfiable"],
		["bytes=99999999999999999999-", 10, "unsatisfiable"],
		["bytes=-0", 10, "unsatisfiable"],
		["bytes=0-", 0, "unsatisfiable"],
		["bytes=0-0", 0, "unsatisfiable"],
		["bytes=-1", 0, "unsatisfiable"],
		["bytes=3-2", 10, null],
		["bytes=0-1,4-5", 10, null],
		["items=0-1", 10, null],
		["Bytes=0-1", 10, null],
		["bytes=", 10, null],
		["bytes=-", 10, null],
		["bytes=a-b", 10, null],
		["bytes=1-2-3", 10, null],
		["bytes= 0-1", 10, null],
		["", 10, null],
	])("parseRange(%j, %i)", (header, size, expected) => {
		expect(parseRange(header, size)).toEqual(expected);
	});

	const hash = "f".repeat(32);
	it.each<[string | null, boolean]>([
		[null, false],
		["", false],
		[`"${hash}"`, true],
		[`W/"${hash}"`, true],
		[`  "${hash}"  `, true],
		[`"other", "${hash}"`, true],
		[`"other",W/"${hash}"`, true],
		["*", true],
		['"other"', false],
		[hash, false],
		[`W/${hash}`, false],
		[`"${hash}x"`, false],
		[`w/"${hash}"`, false],
	])("etagMatches(%j)", (header, expected) => {
		expect(etagMatches(header, hash)).toBe(expected);
	});
});
