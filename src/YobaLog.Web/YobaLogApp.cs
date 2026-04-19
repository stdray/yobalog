using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YobaLog.Core;
using YobaLog.Core.Admin;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Auth;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Kql;
using YobaLog.Core.Retention;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.SelfLogging;
using YobaLog.Core.Sharing;
using YobaLog.Core.Sharing.Sqlite;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Web;

// Same wiring Program.cs uses, factored out so integration tests can build a Kestrel-hosted
// app on an ephemeral port without going through WebApplicationFactory (which hard-codes TestServer).
public static class YobaLogApp
{
	public static void ConfigureServices(WebApplicationBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		builder.Services.Configure<SqliteLogStoreOptions>(builder.Configuration.GetSection("SqliteLogStore"));
		builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
		builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"));
		builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
		builder.Services.Configure<SystemLoggerOptions>(builder.Configuration.GetSection("SystemLogger"));
		builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("Admin"));
		builder.Services.Configure<ShareOptions>(builder.Configuration.GetSection("Share"));

		builder.Services.AddSingleton<ILogStore, SqliteLogStore>();
		builder.Services.AddSingleton<ISavedQueryStore, SqliteSavedQueryStore>();
		builder.Services.AddSingleton<IFieldMaskingPolicyStore, SqliteFieldMaskingPolicyStore>();
		builder.Services.AddSingleton<IShareLinkStore, SqliteShareLinkStore>();
		builder.Services.AddSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
		builder.Services.AddSingleton<IApiKeyStore, ConfigApiKeyStore>();
		builder.Services.AddSingleton<ICleFParser, CleFParser>();
		builder.Services.AddSingleton<KqlCompletionService>();
		builder.Services.AddSingleton<InMemoryTailBroadcaster>();
		builder.Services.AddSingleton<ITailBroadcaster>(sp => sp.GetRequiredService<InMemoryTailBroadcaster>());
		builder.Services.AddSingleton<IRazorPartialRenderer, RazorPartialRenderer>();
		builder.Services.AddSingleton<ChannelIngestionPipeline>();
		builder.Services.AddSingleton<IIngestionPipeline>(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
		builder.Services.AddSingleton<SystemLoggerProvider>();
		builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<SystemLoggerProvider>());

		builder.Services.AddHostedService<WorkspaceBootstrapper>();
		builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
		builder.Services.AddHostedService<RetentionService>();
		builder.Services.AddHostedService<SystemLogFlusher>();

		builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
			.AddCookie(o =>
			{
				o.LoginPath = "/Login";
				o.AccessDeniedPath = "/Login";
				o.ExpireTimeSpan = TimeSpan.FromDays(7);
				o.SlidingExpiration = true;
			});

		builder.Services.AddAuthorizationBuilder()
			.SetFallbackPolicy(new AuthorizationPolicyBuilder()
				.RequireAuthenticatedUser()
				.Build());

		builder.Services.AddRazorPages();
	}

	public static void Configure(WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		if (!app.Environment.IsDevelopment())
		{
			app.UseExceptionHandler("/Error");
			app.UseHsts();
		}

		app.UseHttpsRedirection();
		app.UseStaticFiles();
		app.UseRouting();
		app.UseAuthentication();
		app.UseAuthorization();

		MapEndpoints(app);
		app.MapRazorPages();
	}

	static void MapEndpoints(WebApplication app)
	{
		// Canonical versioned ingestion, one per wire format.
		// Adding a new format = one more MapPost with a format-specific handler; nothing else moves.
		app.MapPost("/api/v1/ingest/clef", IngestionHandlers.CleF).AllowAnonymous();

		// Compatibility surface for third-party clients. Each vendor gets its own slot under
		// /compat/<tech>/ so a future HEC / statsd / GELF receiver doesn't share the seq
		// prefix. Seq clients (Serilog.Sinks.Seq, seq-logging, seqlog) hardcode the trailing
		// "/api/events/raw" and just string-concat it to the base URL, so any path prefix
		// works as long as the suffix stays. Users configure their client base URL as
		// `https://yobalog/compat/seq` — the client produces `…/compat/seq/api/events/raw`.
		app.MapPost("/compat/seq/api/events/raw", IngestionHandlers.CleF).AllowAnonymous();

		app.MapPost("/Logout", async (HttpContext ctx) =>
		{
			await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return Results.Redirect("/Login");
		});

		app.MapGet("/api/kql/completions", async (
			[FromQuery(Name = "q")] string? query,
			[FromQuery(Name = "pos")] int? position,
			[FromQuery(Name = "ws")] string? ws,
			KqlCompletionService completions,
			ILogStore store,
			CancellationToken ct) =>
		{
			var q = query ?? "";
			var p = Math.Clamp(position ?? 0, 0, q.Length);

			if (PropertyKeyContext.TryMatch(q, p, out var editStart, out var prefix)
				&& !string.IsNullOrEmpty(ws)
				&& WorkspaceId.TryParse(ws, out var workspace))
			{
				var keys = await store.GetPropertyKeysAsync(workspace, ct);
				var items = keys
					.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					.Take(KqlCompletionService.MaxItems)
					.Select(k => new KqlCompletionItem("Property", k, k, ""))
					.ToList();
				return Results.Extensions.CompletionsHtml(new KqlCompletionsResponse(editStart, prefix.Length, items));
			}

			var result = completions.Complete(q, p);
			return Results.Extensions.CompletionsHtml(result);
		});

		app.MapGet("/api/ws/{id}/tail", (
			string id,
			[FromQuery(Name = "kql")] string? kql,
			ITailBroadcaster broadcaster,
			IRazorPartialRenderer renderer) =>
		{
			if (!WorkspaceId.TryParse(id, out var ws))
				return Results.NotFound();

			var code = Kusto.Language.KustoCode.Parse(string.IsNullOrWhiteSpace(kql) ? "events" : kql);
			var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
			if (errors.Count > 0)
				return Results.BadRequest("KQL parse error: " + string.Join("; ", errors.Select(d => d.Message)));

			return Results.Extensions.LiveTail(ws, code, broadcaster, renderer);
		});

		app.MapPost("/api/ws/{id}/share", async (
			string id,
			ShareRequest req,
			HttpContext ctx,
			IFieldMaskingPolicyStore policyStore,
			IShareLinkStore shareLinks,
			IOptions<ShareOptions> shareOptions,
			CancellationToken ct) =>
		{
			if (!WorkspaceId.TryParse(id, out var ws))
				return Results.NotFound();

			var defaultTtl = shareOptions.Value.DefaultTtlHours;
			var ttlHours = req.TtlHours is int t && t > 0 ? t : defaultTtl;
			var expiresAt = DateTimeOffset.UtcNow.AddHours(ttlHours);

			var modes = (req.Modes ?? [])
				.Where(kv => Enum.TryParse<MaskMode>(kv.Value, ignoreCase: true, out _))
				.ToImmutableDictionary(
					kv => kv.Key,
					kv => Enum.Parse<MaskMode>(kv.Value, ignoreCase: true),
					StringComparer.Ordinal);

			var columns = (req.Columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToImmutableArray();

			var link = await shareLinks.CreateAsync(ws, req.Kql ?? "events", expiresAt, columns, modes, ct);

			if (req.SavePolicy == true && modes.Count > 0)
				await policyStore.UpsertAsync(ws, modes, ct);

			var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/share/{ws.Value}/{link.Id}.tsv";
			return Results.Ok(new ShareResponse(url, expiresAt));
		});

		app.MapGet("/share/{ws}/{id}.tsv", async (
			string ws,
			string id,
			HttpContext ctx,
			IShareLinkStore shareLinks,
			ILogStore store,
			IOptions<ShareOptions> shareOptions,
			CancellationToken ct) =>
		{
			if (!WorkspaceId.TryParse(ws, out var workspace))
				return Results.NotFound();

			var link = await shareLinks.GetAsync(workspace, id, ct);
			if (link is null)
				return Results.NotFound();

			if (link.ExpiresAt < DateTimeOffset.UtcNow)
			{
				await shareLinks.DeleteAsync(workspace, id, ct);
				return Results.StatusCode(StatusCodes.Status410Gone);
			}

			var userKql = string.IsNullOrWhiteSpace(link.Kql) ? "events" : link.Kql.Trim();
			var effectiveKql = userKql
				+ "\n| order by Timestamp desc, Id desc"
				+ $"\n| take {shareOptions.Value.MaxRows}";

			Kusto.Language.KustoCode code;
			try
			{
				code = Kusto.Language.KustoCode.Parse(effectiveKql);
				var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
				if (errors.Count > 0)
					return Results.BadRequest("KQL parse error: " + string.Join("; ", errors.Select(d => d.Message)));
			}
			catch (Exception ex)
			{
				return Results.BadRequest(ex.Message);
			}

			ctx.Response.ContentType = "text/tab-separated-values; charset=utf-8";
			ctx.Response.Headers.CacheControl = "no-store";
			var masker = new ValueMasker(link.Salt.AsSpan());
			var policy = new FieldMaskingPolicy(link.Modes);

			await using var bodyWriter = new StreamWriter(ctx.Response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
			try
			{
				await TsvExporter.WriteAsync(store.QueryKqlAsync(workspace, code, ct), link.Columns, policy, masker, bodyWriter, ct);
			}
			catch (YobaLog.Core.Kql.UnsupportedKqlException ex)
			{
				return Results.BadRequest(ex.Message);
			}

			return Results.Empty;
		}).AllowAnonymous();
	}
}

public sealed record IngestResponse(int Received, int Errors);

public sealed record ShareRequest(string? Kql, int? TtlHours, string[]? Columns, Dictionary<string, string>? Modes, bool? SavePolicy);

public sealed record ShareResponse(string Url, DateTimeOffset ExpiresAt);
