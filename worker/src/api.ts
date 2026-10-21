import { getCatalog } from "./catalog";
import { CORS, json, jsonText, shouldUseCache } from "./http";

const INFO_PREFIX = "/api/info/";
const API_HEADERS = { ...CORS, "Cache-Control": "public, max-age=60" };

// 同一个字符串构造两个 Response（一个写缓存、一个返回），不用 tee
function cacheableJson(body: string, ctx: ExecutionContext, url: URL, useCache: boolean): Response {
	if (useCache) {
		ctx.waitUntil(caches.default.put(new Request(url.toString()), jsonText(body, 200, API_HEADERS)));
	}
	return jsonText(body, 200, API_HEADERS);
}

// 不支持 prefix 参数，需要筛选时由前端自行处理
export async function handleList(request: Request, env: Env, ctx: ExecutionContext, url: URL): Promise<Response> {
	const useCache = shouldUseCache(url);
	if (useCache) {
		const hit = await caches.default.match(request);
		if (hit) {
			return hit;
		}
	}
	return cacheableJson(await getCatalog(env).listJson(), ctx, url, useCache);
}

export async function handleInfo(request: Request, env: Env, ctx: ExecutionContext, url: URL): Promise<Response> {
	let key: string;
	try {
		key = decodeURIComponent(url.pathname.slice(INFO_PREFIX.length));
	} catch {
		return json({ error: "invalid_key" }, 400, CORS);
	}
	if (!key.includes("/")) {
		return json({ error: "invalid_key" }, 400, CORS);
	}

	const useCache = shouldUseCache(url);
	if (useCache) {
		const hit = await caches.default.match(request);
		if (hit) {
			return hit;
		}
	}
	const info = await getCatalog(env).info(key);
	if (info === null) {
		return json({ error: "not_found" }, 404, CORS);
	}
	return cacheableJson(JSON.stringify(info), ctx, url, useCache);
}
