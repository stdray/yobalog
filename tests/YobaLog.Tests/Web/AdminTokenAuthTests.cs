using System.Net;
using System.Net.Http.Json;

namespace YobaLog.Tests.Web;

public sealed class AdminTokenAuthTests : IAsyncLifetime
{
    readonly AdminApiTestFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task Bearer_Header_Accepted()
    {
        using var client = _fx.Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/workspaces");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _fx.AdminToken);
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Custom_Header_Accepted()
    {
        using var client = _fx.Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/workspaces");
        req.Headers.Add("X-YobaLog-AdminToken", _fx.AdminToken);
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Query_Param_Accepted()
    {
        using var client = _fx.Factory.CreateClient();
        using var resp = await client.GetAsync($"/v1/admin/workspaces?adminToken={Uri.EscapeDataString(_fx.AdminToken)}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_Token_Returns401()
    {
        using var client = _fx.Factory.CreateClient();
        using var resp = await client.GetAsync("/v1/admin/workspaces");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        body!.Error.Should().Be("unauthorized");
    }

    [Fact]
    public async Task Invalid_Token_Returns401()
    {
        using var client = _fx.Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/workspaces");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bearer_And_Custom_Header_With_Same_Value_Accepted()
    {
        using var client = _fx.Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/workspaces");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _fx.AdminToken);
        req.Headers.Add("X-YobaLog-AdminToken", _fx.AdminToken);
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bearer_And_Custom_Header_Disagree_Returns400_Ambiguous()
    {
        using var client = _fx.Factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/workspaces");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _fx.AdminToken);
        req.Headers.Add("X-YobaLog-AdminToken", "different-token-value");
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
        body!.Error.Should().Be("ambiguous_auth");
    }

    [Fact]
    public async Task Soft_Deleted_Token_Returns401()
    {
        using var client = _fx.Factory.CreateClient();

        // Create a second token, then soft-delete it via the admin store directly.
        var admin = (YobaLog.Core.Auth.IAdminTokenAdmin)_fx.Factory.Services.GetService(typeof(YobaLog.Core.Auth.IAdminTokenAdmin))!;
        var doomed = await admin.CreateAsync(_fx.Username, "to-be-revoked", CancellationToken.None);
        await admin.SoftDeleteAsync(doomed.Info.Id, CancellationToken.None);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/workspaces");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", doomed.Plaintext);
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    sealed record ErrorBody(string Error, string Reason);
}
