using System.Net;
using System.Net.Http.Json;

namespace YobaLog.Tests.Web;

public sealed class AdminApiKeysEndpointTests : IAsyncLifetime
{
    readonly AdminApiTestFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task Put_Returns_Plaintext_Once_201()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });

        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/api-keys",
            new { title = "yobapub-server" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<ApiKeyBody>();
        body!.Plaintext.Should().NotBeNullOrWhiteSpace();
        body.Plaintext!.Should().HaveLength(22);
        body.Prefix.Should().Be(body.Plaintext[..6]);
        body.Title.Should().Be("yobapub-server");
    }

    [Fact]
    public async Task List_Excludes_Plaintext()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });
        await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/api-keys", new { title = "k1" });

        var keys = await client.GetFromJsonAsync<ApiKeyBody[]>("/v1/admin/workspaces/alpha/api-keys");
        keys!.Should().HaveCount(1);
        keys[0].Plaintext.Should().BeNull();
        keys[0].Prefix.Should().NotBeNullOrWhiteSpace();
        keys[0].Title.Should().Be("k1");
    }

    [Fact]
    public async Task Delete_Hides_Key_From_List()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });

        var created = await (await client.PutAsJsonAsync("/v1/admin/workspaces/alpha/api-keys", new { title = "to-drop" }))
            .Content.ReadFromJsonAsync<ApiKeyBody>();

        using var del = await client.DeleteAsync($"/v1/admin/workspaces/alpha/api-keys/{created!.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var keys = await client.GetFromJsonAsync<ApiKeyBody[]>("/v1/admin/workspaces/alpha/api-keys");
        keys!.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Unknown_404()
    {
        using var client = _fx.CreateAuthedClient();
        await client.PutAsJsonAsync("/v1/admin/workspaces", new { id = "alpha" });

        using var resp = await client.DeleteAsync("/v1/admin/workspaces/alpha/api-keys/no-such-id");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_Unknown_Workspace_404()
    {
        using var client = _fx.CreateAuthedClient();
        using var resp = await client.PutAsJsonAsync("/v1/admin/workspaces/never/api-keys", new { title = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_Token_401()
    {
        using var client = _fx.Factory.CreateClient();
        using var resp = await client.GetAsync("/v1/admin/workspaces/alpha/api-keys");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    sealed record ApiKeyBody(string Id, string Prefix, string? Plaintext, string? Title, DateTimeOffset CreatedAt);
}
