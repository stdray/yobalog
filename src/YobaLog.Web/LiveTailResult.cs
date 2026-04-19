using Kusto.Language;
using YobaLog.Core;
using YobaLog.Core.Ingestion;

namespace YobaLog.Web;

static class LiveTailExtensions
{
	public static IResult LiveTail(
		this IResultExtensions _,
		WorkspaceId workspaceId,
		KustoCode query,
		ITailBroadcaster broadcaster,
		IRazorPartialRenderer renderer) =>
		new LiveTailResult(workspaceId, query, broadcaster, renderer);
}

sealed class LiveTailResult(
	WorkspaceId ws,
	KustoCode query,
	ITailBroadcaster broadcaster,
	IRazorPartialRenderer renderer) : IResult
{
	const string PartialName = "_EventRow";

	public async Task ExecuteAsync(HttpContext httpContext)
	{
		httpContext.Response.Headers.ContentType = "text/event-stream";
		httpContext.Response.Headers.CacheControl = "no-cache";
		httpContext.Response.Headers["X-Accel-Buffering"] = "no";
		await httpContext.Response.Body.FlushAsync().ConfigureAwait(false);

		var ct = httpContext.RequestAborted;
		await foreach (var candidate in broadcaster.Subscribe(ws, query, ct).ConfigureAwait(false))
		{
			var vm = EventRowViewModel.FromLive(candidate);
			var html = await renderer.RenderAsync(PartialName, vm, httpContext).ConfigureAwait(false);
			await WriteSseFrameAsync(httpContext.Response, "event", Flatten(html), ct).ConfigureAwait(false);
		}
	}

	static string Flatten(string html) =>
		html.Replace("\r\n", " ", StringComparison.Ordinal)
			.Replace('\n', ' ')
			.Replace('\r', ' ');

	static async Task WriteSseFrameAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
	{
		var frame = $"event: {eventName}\ndata: {data}\n\n";
		await response.WriteAsync(frame, ct).ConfigureAwait(false);
		await response.Body.FlushAsync(ct).ConfigureAwait(false);
	}
}
