using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.Auth;

namespace YobaLog.Tests.Web;

// Shared bring-up for /v1/admin/* integration tests. Spins a WebApplicationFactory, drives
// IAdminTokenAdmin.CreateAsync to mint a usable token, hands the plaintext back to the test.
// Async-only disposal — owners run it from IAsyncLifetime.DisposeAsync.
sealed class AdminApiTestFixture : IAsyncDisposable
{
    public string TempDir { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public string AdminToken { get; private set; } = string.Empty;
    public string Username { get; } = "alice";

    public AdminApiTestFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "yobalog-admin-api-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(TempDir);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Testing");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["SqliteLogStore:DataDirectory"] = TempDir,
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        // Touch services so WorkspaceBootstrapper runs (creates schemas + $system workspace).
        _ = Factory.CreateClient();
        var admin = Factory.Services.GetRequiredService<IAdminTokenAdmin>();
        var created = await admin.CreateAsync(Username, "test-fixture", CancellationToken.None);
        AdminToken = created.Plaintext;
    }

    public HttpClient CreateAuthedClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        try { Directory.Delete(TempDir, recursive: true); }
        catch { /* best effort */ }
    }
}
