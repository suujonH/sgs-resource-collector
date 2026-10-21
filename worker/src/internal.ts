import { blobKey, getCatalog, type CatalogVersion } from "./catalog";
import { NO_STORE, json, jsonText } from "./http";

const BLOB_ROUTE = "/internal/blobs/";
const HASH_PATTERN = /^[0-9a-f]{32}$/;
const MAX_SYNC_ROWS = 1000;
const MAX_VERSION = 2147483647;
const DEFAULT_EXPORT_LIMIT = 1000;
const MAX_EXPORT_LIMIT = 2000;

// 缺少密钥时一律拒绝；先比长度（timingSafeEqual 要求等长），再做常量时间比较
export function authorized(request: Request, env: Env): boolean {
	const token = env.SYNC_TOKEN;
	if (!token) {
		return false;
	}
	const encoder = new TextEncoder();
	const actual = encoder.encode(request.headers.get("Authorization") ?? "");
	const expected = encoder.encode(`Bearer ${token}`);
	if (actual.byteLength !== expected.byteLength) {
		return false;
	}
	return crypto.subtle.timingSafeEqual(actual, expected);
}

export async function handleInternal(request: Request, env: Env, url: URL): Promise<Response> {
	if (!authorized(request, env)) {
		return json({ error: "unauthorized" }, 401);
	}
	const { pathname } = url;
	if (request.method === "PUT" && pathname.startsWith(BLOB_ROUTE)) {
		return putBlob(request, env, pathname.slice(BLOB_ROUTE.length));
	}
	if (request.method === "POST" && pathname === "/internal/sync") {
		return postSync(request, env);
	}
	if (request.method === "GET" && pathname === "/internal/stats") {
		return json(await getCatalog(env).counts(), 200, NO_STORE);
	}
	if (request.method === "GET" && pathname === "/internal/export") {
		return getExport(env, url);
	}
	return json({ error: "not_found" }, 404);
}

async function putBlob(request: Request, env: Env, hash: string): Promise<Response> {
	if (!HASH_PATTERN.test(hash)) {
		return json({ error: "invalid_hash" }, 400);
	}
	// R2 的 put 需要已知长度的流
	if (request.headers.get("Content-Length") === null) {
		return json({ error: "length_required" }, 411);
	}
	if (request.body === null) {
		return json({ error: "missing_body" }, 400);
	}
	const obj = await env.BLOBS.put(blobKey(hash), request.body, {
		httpMetadata: { contentType: request.headers.get("Content-Type") ?? "application/octet-stream" },
	});
	return json({ ok: true, size: obj?.size ?? 0 }, 200, NO_STORE);
}

async function postSync(request: Request, env: Env): Promise<Response> {
	let payload: unknown;
	try {
		payload = await request.json();
	} catch {
		return json({ error: "invalid_json" }, 400);
	}
	const versions = isRecord(payload) ? payload.versions : undefined;
	if (!Array.isArray(versions) || versions.length < 1 || versions.length > MAX_SYNC_ROWS) {
		return json({ error: "invalid_body" }, 400);
	}

	// 不合法的行单独报告，不让整批失败
	const validRows: CatalogVersion[] = [];
	const rejected: { index: number; reason: string }[] = [];
	versions.forEach((raw: unknown, index: number) => {
		const result = validateRow(raw);
		if ("row" in result) {
			validRows.push(result.row);
		} else {
			rejected.push({ index, reason: result.reason });
		}
	});

	const applied = validRows.length === 0 ? { accepted: 0, conflicts: [] } : await getCatalog(env).applyVersions(validRows);
	return json({ accepted: applied.accepted, rejected, conflicts: applied.conflicts }, 200, NO_STORE);
}

function validateRow(raw: unknown): { row: CatalogVersion } | { reason: string } {
	if (!isRecord(raw)) {
		return { reason: "invalid_row" };
	}
	const { key, version, hash, size, contentType, collectedAt } = raw;
	if (typeof key !== "string" || key.length < 3 || key.length > 2048 || !key.includes("/")) {
		return { reason: "invalid_key" };
	}
	if (typeof version !== "number" || !Number.isInteger(version) || version < 1 || version > MAX_VERSION) {
		return { reason: "invalid_version" };
	}
	if (typeof hash !== "string" || !HASH_PATTERN.test(hash)) {
		return { reason: "invalid_hash" };
	}
	if (typeof size !== "number" || !Number.isSafeInteger(size) || size < 0) {
		return { reason: "invalid_size" };
	}
	if (typeof contentType !== "string" || contentType.length < 1 || contentType.length > 200 || /[^\x20-\x7E]/.test(contentType)) {
		return { reason: "invalid_content_type" };
	}
	if (typeof collectedAt !== "string" || collectedAt.length < 1 || collectedAt.length > 40) {
		return { reason: "invalid_collected_at" };
	}
	return { row: { key, version, hash, size, contentType, collectedAt } };
}

async function getExport(env: Env, url: URL): Promise<Response> {
	const afterKey = url.searchParams.get("afterKey") ?? "";
	const afterVersion = parseIntegerParam(url.searchParams.get("afterVersion"), 0);
	const limit = parseIntegerParam(url.searchParams.get("limit"), DEFAULT_EXPORT_LIMIT);
	if (afterVersion === null || afterVersion < 0 || limit === null) {
		return json({ error: "invalid_query" }, 400);
	}
	const body = await getCatalog(env).exportJson(afterKey, afterVersion, Math.min(Math.max(limit, 1), MAX_EXPORT_LIMIT));
	return jsonText(body, 200, NO_STORE);
}

// 缺省或为空时取 fallback；不是整数时返回 null
function parseIntegerParam(value: string | null, fallback: number): number | null {
	if (value === null || value === "") {
		return fallback;
	}
	const n = Number(value);
	return Number.isInteger(n) ? n : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}
