using System.Reflection;
using Microsoft.Playwright;

namespace YobaLog.E2ETests.Infrastructure;

// Playwright tracing → `tests/YobaLog.E2ETests/bin/<cfg>/<tfm>/artifacts/<test>.zip`.
// Always-on recording + always-save pattern. Local dev: ~500KB per test, gitignored, user deletes
// when tired. CI: `upload-artifact: if: failure()` uploads only when the job failed, so greens
// cost nothing. Avoids xUnit 2.x's lack of test-outcome access in DisposeAsync — we just keep
// everything and let the CI step discriminate.
public static class TraceArtifact
{
	static readonly string ArtifactsDir = Path.Combine(AppContext.BaseDirectory, "artifacts");

	public static async Task StartAsync(IBrowserContext ctx) =>
		await ctx.Tracing.StartAsync(new TracingStartOptions
		{
			Screenshots = true,
			Snapshots = true,
			Sources = true,
		});

	public static async Task StopAndSaveAsync(IBrowserContext ctx, ITestOutputHelper output)
	{
		Directory.CreateDirectory(ArtifactsDir);
		var slug = Sanitize(ExtractTestName(output));
		var path = Path.Combine(ArtifactsDir, slug + ".zip");
		await ctx.Tracing.StopAsync(new TracingStopOptions { Path = path });
	}

	// xUnit 2.x's ITestOutputHelper has a non-public `test` field that carries the running
	// ITest instance. DisplayName = `Namespace.Class.Method(params)` for a [Fact]. Stable across
	// 2.x releases; if a future xUnit breaks this, the fallback is a guid'd filename.
	static string ExtractTestName(ITestOutputHelper output)
	{
		var field = output.GetType().GetField("test", BindingFlags.Instance | BindingFlags.NonPublic);
		if (field?.GetValue(output) is ITest test)
			return test.DisplayName;
		return "unknown-" + Guid.NewGuid().ToString("N")[..8];
	}

	static string Sanitize(string s)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var chars = s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
		var result = new string(chars);
		// Windows MAX_PATH — artifacts dir + file name combined; keep it short.
		return result.Length > 120 ? result[..120] : result;
	}
}
