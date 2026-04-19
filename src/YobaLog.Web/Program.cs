using YobaLog.Core;
using YobaLog.Core.Auth;
using YobaLog.Core.Ingestion;
using YobaLog.Core.Retention;
using YobaLog.Core.SelfLogging;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SqliteLogStoreOptions>(builder.Configuration.GetSection("SqliteLogStore"));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.Configure<SystemLoggerOptions>(builder.Configuration.GetSection("SystemLogger"));

builder.Services.AddSingleton<ILogStore, SqliteLogStore>();
builder.Services.AddSingleton<IApiKeyStore, ConfigApiKeyStore>();
builder.Services.AddSingleton<ICleFParser, CleFParser>();
builder.Services.AddSingleton<ChannelIngestionPipeline>();
builder.Services.AddSingleton<IIngestionPipeline>(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
builder.Services.AddSingleton<SystemLoggerProvider>();
builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<SystemLoggerProvider>());

builder.Services.AddHostedService<WorkspaceBootstrapper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<SystemLogFlusher>();

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
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
});

app.MapStaticAssets();
app.MapRazorPages()
	.WithStaticAssets();

app.Run();

internal sealed record IngestResponse(int Received, int Errors);

public partial class Program;
