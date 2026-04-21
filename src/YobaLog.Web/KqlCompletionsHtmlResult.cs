using YobaLog.Core.Kql;

namespace YobaLog.Web;

static class KqlCompletionsHtmlExtensions
{
	public static IResult CompletionsHtml(
		this IResultExtensions _,
		KqlCompletionsResponse response,
		IRazorPartialRenderer renderer)
	{
		ArgumentNullException.ThrowIfNull(response);
		ArgumentNullException.ThrowIfNull(renderer);
		return new KqlCompletionsHtmlResult(response, renderer);
	}
}

sealed class KqlCompletionsHtmlResult(
	KqlCompletionsResponse response,
	IRazorPartialRenderer renderer) : IResult
{
	const string PartialName = "_KqlCompletions";

	public async Task ExecuteAsync(HttpContext httpContext)
	{
		httpContext.Response.ContentType = "text/html; charset=utf-8";
		var html = await renderer.RenderAsync(PartialName, response, httpContext).ConfigureAwait(false);
		await httpContext.Response.WriteAsync(html).ConfigureAwait(false);
	}
}
