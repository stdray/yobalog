using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace YobaLog.Web;

public interface IRazorPartialRenderer
{
	Task<string> RenderAsync<TModel>(string partialName, TModel model, HttpContext httpContext);
}

public sealed class RazorPartialRenderer : IRazorPartialRenderer
{
	readonly IRazorViewEngine _viewEngine;
	readonly ITempDataProvider _tempDataProvider;
	readonly IModelMetadataProvider _modelMetadata;

	public RazorPartialRenderer(
		IRazorViewEngine viewEngine,
		ITempDataProvider tempDataProvider,
		IModelMetadataProvider modelMetadata)
	{
		_viewEngine = viewEngine;
		_tempDataProvider = tempDataProvider;
		_modelMetadata = modelMetadata;
	}

	public async Task<string> RenderAsync<TModel>(string partialName, TModel model, HttpContext httpContext)
	{
		var actionContext = new ActionContext(
			httpContext,
			httpContext.GetRouteData() ?? new RouteData(),
			new ActionDescriptor());

		var viewResult = _viewEngine.GetView(executingFilePath: null, partialName, isMainPage: false);
		if (!viewResult.Success)
			viewResult = _viewEngine.FindView(actionContext, partialName, isMainPage: false);
		if (!viewResult.Success)
			throw new InvalidOperationException(
				$"Partial view '{partialName}' not found. Searched: {string.Join("; ", viewResult.SearchedLocations ?? [])}");

		var viewData = new ViewDataDictionary<TModel>(_modelMetadata, new ModelStateDictionary())
		{
			Model = model,
		};
		using var writer = new StringWriter();
		var viewContext = new ViewContext(
			actionContext,
			viewResult.View,
			viewData,
			new TempDataDictionary(httpContext, _tempDataProvider),
			writer,
			new HtmlHelperOptions());

		await viewResult.View.RenderAsync(viewContext).ConfigureAwait(false);
		return writer.ToString();
	}
}
