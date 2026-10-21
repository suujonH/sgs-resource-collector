import { blobKey, getCatalog, type ResolvedVersion } from "./catalog";
import { CORS, NO_STORE, json, shouldUseCache } from "./http";

const RESOURCE_PREFIX = "/resource/";

export function parseResourcePath(rest: string): { key: string; version: number | null } | null {
	if (rest === "") {
		return null;
	}
	const segs = rest.split("/");
	let version: number | null = null;
	let raw = rest;
	if (/^[1-9]\d{0,8}$/.test(segs[0]) && segs.length >= 2) {
		version = Number(segs[0]);
		raw = segs.slice(1).join("/");
	}
	let key: string;
	try {
		key = decodeURIComponent(raw);
	} catch {
		return null;
	}
	if (!key.includes("/")) {
		return null;
	}
	return { key, version };
}

// null 表示忽略 Range、返回完整内容
export function parseRange(header: string, size: number): { offset: number; length: number } | "unsatisfiable" | null {
	if (!header.startsWith("bytes=") || header.includes(",")) {
		return null;
	}
	const match = /^(\d*)-(\d*)$/.exec(header.slice("bytes=".length));
	if (match === null) {
		return null;
	}
	const [, first, last] = match;
	if (first === "" && last === "") {
		return null;
	}
	if (first === "") {
		const n = Number(last);
		if (n === 0 || size === 0) {
			return "unsatisfiable";
		}
		const length = Math.min(n, size);
		return { offset: size - length, length };
	}
	const start = Number(first);
	if (last === "") {
		return start >= size ? "unsatisfiable" : { offset: start, length: size - start };
	}
	const end = Number(last);
	if (start > end) {
		return null;
	}
	if (start >= size) {
		return "unsatisfiable";
	}
	return { offset: start, length: Math.min(end, size - 1) - start + 1 };
}

// Cloudflare 压缩文本时会把强 ETag 变成弱 ETag，所以去掉 W/ 再比较
export function etagMatches(header: string | null, hash: string): boolean {
	if (header === null) {
		return false;
	}
	const strong = `"${hash}"`;
	return header.split(",").some((item) => {
		let tag = item.trim();
		if (tag.startsWith("W/")) {
			tag = tag.slice(2);
		}
		return tag === "*" || tag === strong;
	});
}

export function resourceHeaders(meta: ResolvedVersion, versioned: boolean): Headers {
	const headers = new Headers(CORS);
	headers.set("Content-Type", meta.contentType);
	headers.set("ETag", `"${meta.hash}"`);
	headers.set("X-Resource-Version", String(meta.version));
	headers.set("Accept-Ranges", "bytes");
	// 归档里有 JS、SVG，防止在本域名执行脚本
	headers.set("X-Content-Type-Options", "nosniff");
	headers.set("Content-Security-Policy", "sandbox");
	headers.set("Cache-Control", versioned ? "public, max-age=31536000, immutable" : "public, max-age=60");
	return headers;
}

export async function handleResource(request: Request, env: Env, url: URL): Promise<Response> {
	const parsed = parseResourcePath(url.pathname.slice(RESOURCE_PREFIX.length));
	if (parsed === null) {
		return json({ error: "invalid_path" }, 400, CORS);
	}

	const useCache = shouldUseCache(url);
	if (useCache && request.method === "GET") {
		// Cloudflare 的 cache.match 会自行处理 Range（206）和 If-None-Match（304）
		const hit = await caches.default.match(request);
		if (hit) {
			return hit;
		}
	}

	const meta = await getCatalog(env).resolve(parsed.key, parsed.version);
	if (meta === null) {
		return json({ error: "not_found" }, 404, CORS);
	}

	const headers = resourceHeaders(meta, parsed.version !== null);
	if (etagMatches(request.headers.get("If-None-Match"), meta.hash)) {
		return new Response(null, { status: 304, headers });
	}
	if (request.method === "HEAD") {
		headers.set("Content-Length", String(meta.size));
		return new Response(null, { status: 200, headers });
	}

	if (useCache) {
		// 先写缓存、再从缓存取：不用 tee，避免在内存中缓冲大文件
		const obj = await env.BLOBS.get(blobKey(meta.hash));
		if (obj === null) {
			return blobMissing();
		}
		headers.set("Content-Length", String(obj.size));
		await caches.default.put(new Request(url.toString()), new Response(obj.body, { status: 200, headers }));
		const cached = await caches.default.match(request);
		if (cached) {
			return cached;
		}
		// 缓存没有生效时，直接从 R2 响应
	}
	return serveFromR2(request, env, meta, headers);
}

async function serveFromR2(request: Request, env: Env, meta: ResolvedVersion, headers: Headers): Promise<Response> {
	const key = blobKey(meta.hash);
	const rangeHeader = request.headers.get("Range");
	const range = rangeHeader === null ? null : parseRange(rangeHeader, meta.size);

	if (range === "unsatisfiable") {
		return new Response(null, { status: 416, headers: { ...CORS, ...NO_STORE, "Content-Range": `bytes */${meta.size}` } });
	}
	if (range !== null) {
		const part = await env.BLOBS.get(key, { range });
		if (part === null) {
			return blobMissing();
		}
		headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${meta.size}`);
		headers.set("Content-Length", String(range.length));
		return new Response(part.body, { status: 206, headers });
	}

	const obj = await env.BLOBS.get(key);
	if (obj === null) {
		return blobMissing();
	}
	headers.set("Content-Length", String(obj.size));
	return new Response(obj.body, { status: 200, headers });
}

function blobMissing(): Response {
	return json({ error: "blob_missing" }, 502, CORS);
}
