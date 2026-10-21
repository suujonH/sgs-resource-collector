namespace Sgs.Collector.Api;

internal static class ApiGuard
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "sgs-collector";

    // D12：修改类请求必须带自定义请求头。跨站页面无法在不触发 CORS 预检的情况下设置它
    public static void Use(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !HttpMethods.IsGet(request.Method)
                && !HttpMethods.IsHead(request.Method)
                && !string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ErrorDto("缺少 X-Requested-With 请求头"));
                return;
            }

            await next(context);
        });
    }
}
