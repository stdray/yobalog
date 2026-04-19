using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YobaLog.Core;
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
using YobaLog.Web;

if (args.Length >= 2 && args[0] == "--hash-password")
{
	Console.WriteLine(AdminPasswordHasher.Hash(args[1]));
	return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SqliteLogStoreOptions>(builder.Configuration.GetSection("SqliteLogStore"));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.Configure<SystemLoggerOptions>(builder.Configuration.GetSection("SystemLogger"));
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<ShareSigningOptions>(builder.Configuration.GetSection("ShareSigning"));

builder.Services.AddSingleton<ILogStore, SqliteLogStore>();
builder.Services.AddSingleton<ISavedQueryStore, SqliteSavedQueryStore>();
builder.Services.AddSingleton<IFieldMaskingPolicyStore, SqliteFieldMaskingPolicyStore>();
builder.Services.AddSingleton<ShareTokenCodec>();
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

var app = builder.Build();

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

app.MapPost("/api/events/raw", async (
	HttpContext ctx,
	IApiKeyStore apiKeys,
	IIngestionPipeline pipeline,
	ICleFParser parser,
	CancellationToken ct) =>
{
	var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
		?? ctx.Request.Query["apiKey"].FirstOrDefault();

	var validation = await apiKeys.ValidateAsync(token, ct);
	if (!validation.IsValid || validation.Scope is not { } scope)
		return Results.Unauthorized();

	var candidates = new List<LogEventCandidate>();
	var errorCount = 0;
	await foreach (var line in parser.ParseAsync(ctx.Request.Body, ct))
	{
		if (line.IsSuccess && line.Event is not null)
			candidates.Add(line.Event);
		else
			errorCount++;
	}

	if (candidates.Count > 0)
		await pipeline.IngestAsync(scope, candidates, ct);

	return Results.Created("/api/events/raw", new IngestResponse(candidates.Count, errorCount));
}).AllowAnonymous();

app.MapPost("/Logout", async (HttpContext ctx) =>
{
	await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
	return Results.Redirect("/Login");
});

app.MapGet("/api/kql/completions", (
	[FromQuery(Name = "q")] string? query,
	[FromQuery(Name = "pos")] int? position,
	KqlCompletionService completions) =>
{
	var result = completions.Complete(query ?? "", position ?? 0);
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

	var code = Kusto.Language.KustoCode.Parse(string.IsNullOrWhiteSpace(kql) ? "LogEvents" : kql);
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
	ShareTokenCodec codec,
	IOptions<ShareSigningOptions> shareOptions,
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

	var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
	var columns = (req.Columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToImmutableArray();

	var tokenStr = codec.Encode(new ShareToken(
		ws,
		req.Kql ?? "LogEvents",
		expiresAt,
		[.. salt],
		columns,
		modes));

	if (req.SavePolicy == true && modes.Count > 0)
		await policyStore.UpsertAsync(ws, modes, ct);

	var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/share/{tokenStr}.tsv";
	return Results.Ok(new ShareResponse(url, expiresAt));
});

app.MapGet("/share/{token}.tsv", async (
	string token,
	HttpContext ctx,
	ShareTokenCodec codec,
	ILogStore store,
	CancellationToken ct) =>
{
	var decoded = codec.Decode(token);
	if (decoded is null)
		return Results.NotFound();

	if (decoded.ExpiresAt < DateTimeOffset.UtcNow)
		return Results.StatusCode(StatusCodes.Status410Gone);

	var opts = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ShareSigningOptions>>().Value;
	var userKql = string.IsNullOrWhiteSpace(decoded.Kql) ? "LogEvents" : decoded.Kql.Trim();
	var effectiveKql = userKql
		+ "\n| order by Timestamp desc, Id desc"
		+ $"\n| take {opts.MaxRows}";

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
	var masker = new ValueMasker(decoded.Salt.AsSpan());
	var policy = new FieldMaskingPolicy(decoded.Modes);

	await using var bodyWriter = new StreamWriter(ctx.Response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
	try
	{
		await TsvExporter.WriteAsync(store.QueryKqlAsync(decoded.Workspace, code, ct), decoded.Columns, policy, masker, bodyWriter, ct);
	}
	catch (YobaLog.Core.Kql.UnsupportedKqlException ex)
	{
		return Results.BadRequest(ex.Message);
	}

	return Results.Empty;
}).AllowAnonymous();

app.MapRazorPages();

app.Run();

internal sealed record IngestResponse(int Received, int Errors);

internal sealed record ShareRequest(string? Kql, int? TtlHours, string[]? Columns, Dictionary<string, string>? Modes, bool? SavePolicy);
internal sealed record ShareResponse(string Url, DateTimeOffset ExpiresAt);

public partial class Program;
