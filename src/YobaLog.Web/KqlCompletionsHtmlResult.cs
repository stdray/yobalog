using System.Net;
using System.Text;
using YobaLog.Core.Kql;

namespace YobaLog.Web;

static class KqlCompletionsHtmlExtensions
{
	public static IResult CompletionsHtml(this IResultExtensions _, KqlCompletionsResponse response)
	{
		ArgumentNullException.ThrowIfNull(response);
		return new KqlCompletionsHtmlResult(response);
	}
}

sealed class KqlCompletionsHtmlResult(KqlCompletionsResponse response) : IResult
{
	public async Task ExecuteAsync(HttpContext httpContext)
	{
		httpContext.Response.ContentType = "text/html; charset=utf-8";
		var html = Render(response);
		await httpContext.Response.WriteAsync(html).ConfigureAwait(false);
	}

	static string Render(KqlCompletionsResponse r)
	{
		if (r.Items.Count == 0)
			return """<div class="hidden" data-kql-completions data-edit-start="0" data-edit-length="0"></div>""";

		var sb = new StringBuilder();
		sb.Append(System.Globalization.CultureInfo.InvariantCulture,
			$"""<ul data-kql-completions data-edit-start="{r.EditStart}" data-edit-length="{r.EditLength}" class="menu bg-base-300 border border-primary/40 rounded-box mt-1 max-h-72 overflow-y-auto absolute z-30 w-full shadow-2xl">""");
		foreach (var item in r.Items)
		{
			sb.Append("<li><button type=\"button\" class=\"kql-suggestion flex justify-between items-center gap-2 text-xs font-mono\" ");
			sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"""data-before="{WebUtility.HtmlEncode(item.BeforeText)}" data-after="{WebUtility.HtmlEncode(item.AfterText)}">""");
			sb.Append(WebUtility.HtmlEncode(item.DisplayText));
			sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"""<span class="opacity-50">{WebUtility.HtmlEncode(item.Kind)}</span>""");
			sb.Append("</button></li>");
		}
		sb.Append("</ul>");
		return sb.ToString();
	}
}
