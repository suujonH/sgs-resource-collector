export const CORS = {
	"Access-Control-Allow-Origin": "*",
	"Access-Control-Expose-Headers": "ETag, X-Resource-Version, Content-Range, Content-Length",
};

// 所有 4xx / 5xx 响应都带上
export const NO_STORE = { "Cache-Control": "no-store" };

const JSON_CONTENT_TYPE = "application/json; charset=utf-8";

// 已经序列化好的 JSON 字符串直接作为 body，避免重复序列化
export function jsonText(text: string, status = 200, extraHeaders?: HeadersInit): Response {
	const headers = new Headers(extraHeaders);
	headers.set("Content-Type", JSON_CONTENT_TYPE);
	if (status >= 400) {
		headers.set("Cache-Control", NO_STORE["Cache-Control"]);
	}
	return new Response(text, { status, headers });
}

export function json(body: unknown, status = 200, extraHeaders?: HeadersInit): Response {
	return jsonText(JSON.stringify(body), status, extraHeaders);
}

export function preflight(): Response {
	return new Response(null, {
		status: 204,
		headers: {
			...CORS,
			"Access-Control-Allow-Methods": "GET, HEAD, OPTIONS",
			"Access-Control-Allow-Headers": "Range, If-None-Match",
			"Access-Control-Max-Age": "86400",
		},
	});
}

// workers.dev 上 Cache API 不生效，只在自定义域名上使用
export function shouldUseCache(url: URL): boolean {
	return !url.hostname.endsWith(".workers.dev");
}
