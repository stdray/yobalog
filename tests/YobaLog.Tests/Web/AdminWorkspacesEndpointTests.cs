using System.Net;
using System.Net.Http.Json;

namespace YobaLog.Tests.Web;

public sealed class AdminWorkspacesEndpointTests : IAsyncLifetime
{
    readonly AdminApiTestFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task Put_CreatesWorkspace_201()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "yobapub" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<WorkspaceBody>();
        body!.Id.Should().Be("yobapub");
        body.CreatedAt.Should().BeAfter(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Put_Idempotent_ReturnsExisting_200()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "yobapub" });

        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "yobapub" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Put_InvalidSlug_400()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "BadCaps" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_SystemPrefix_400()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "$tampered" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_MissingBody_400()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.PutAsJsonAsync<object?>("/v1/admin/workspaces", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_HidesSystemWorkspace()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "beta" });

        var rows = await client.GetFromJsonAsync<WorkspaceBody[]>("/v1/admin/workspaces");
        rows!.Select(r => r.Id).Should().BeEquivalentTo(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task GetSingle_404_OnUnknown()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.GetAsync("/v1/admin/workspaces/missing");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesWorkspace_204()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "to-drop" });

        using var resp = await client.DeleteAsync("/v1/admin/workspaces/to-drop");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var get = await client.GetAsync("/v1/admin/workspaces/to-drop");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Unknown_404()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.DeleteAsync("/v1/admin/workspaces/never-existed");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Without_Token_401()
    {
        using var client = _fx.Factory.CreateClient();
        using var resp = await client.GetAsync("/v1/admin/workspaces");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    sealed record WorkspaceBody(string Id, DateTimeOffset CreatedAt);
}
