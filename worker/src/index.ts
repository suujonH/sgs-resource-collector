import { handleInfo, handleList } from "./api";
import { CORS, json, preflight } from "./http";
import { handleInternal } from "./internal";
import { handleResource } from "./resource";

export { Catalog } from "./catalog";

export default {
	async fetch(request, env, ctx): Promise<Response> {
		const url = new URL(request.url);
		const { pathname } = url;
		const { method } = request;

		if (pathname.startsWith("/internal/")) {
			return handleInternal(request, env, url);
		}
		if (method === "OPTIONS") {
			return preflight();
		}
		if (pathname.startsWith("/resource/") && (method === "GET" || method === "HEAD")) {
			return handleResource(request, env, url);
		}
		if (method === "GET" && pathname === "/api/list") {
			return handleList(request, env, ctx, url);
		}
		if (method === "GET" && pathname.startsWith("/api/info/")) {
			return handleInfo(request, env, ctx, url);
		}
		return json({ error: "not_found" }, 404, CORS);
	},
} satisfies ExportedHandler<Env>;
