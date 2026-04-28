using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YobaLog.Core.SavedQueries;

namespace YobaLog.Tests.Web;

public sealed class AdminRetentionEndpointTests : IAsyncLifetime
{
    readonly AdminApiTestFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task Put_Then_Get_Roundtrip()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });
        await SeedSavedQueryAsync("alpha", "errors-only");

        using var put = await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/retention",
            new { savedQuery = "errors-only", retainDays = 60 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<RetentionBody[]>("/v1/admin/workspaces/alpha/retention");
        rows!.Should().HaveCount(1);
        rows[0].SavedQuery.Should().Be("errors-only");
        rows[0].RetainDays.Should().Be(60);
    }

    [Fact]
    public async Task Put_Updates_Existing_Policy()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });
        await SeedSavedQueryAsync("alpha", "errors-only");

        await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/retention",
            new { savedQuery = "errors-only", retainDays = 30 });
        await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/retention",
            new { savedQuery = "errors-only", retainDays = 90 });

        var rows = await client.GetFromJsonAsync<RetentionBody[]>("/v1/admin/workspaces/alpha/retention");
        rows!.Should().HaveCount(1);
        rows[0].RetainDays.Should().Be(90);
    }

    [Fact]
    public async Task Put_NonPositive_RetainDays_400()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });
        await SeedSavedQueryAsync("alpha", "errors-only");

        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/retention",
            new { savedQuery = "errors-only", retainDays = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_Unknown_SavedQuery_404()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });

        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/retention",
            new { savedQuery = "ghost", retainDays = 30 });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Removes_Policy_204()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });
        await SeedSavedQueryAsync("alpha", "errors-only");
        await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/retention",
            new { savedQuery = "errors-only", retainDays = 30 });

        using var del = await client.DeleteAsync("/v1/admin/workspaces/alpha/retention/errors-only");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await client.GetFromJsonAsync<RetentionBody[]>("/v1/admin/workspaces/alpha/retention");
        rows!.Should().BeEmpty();
    }

    async Task SeedSavedQueryAsync(string workspace, string name)
    {
        var saved = _fx.Factory.Services.GetRequiredService<ISavedQueryStore>();
        await saved.UpsertAsync(WorkspaceId.Parse(workspace), name, "events", CancellationToken.None);
    }

    sealed record RetentionBody(string SavedQuery, int RetainDays);
}
