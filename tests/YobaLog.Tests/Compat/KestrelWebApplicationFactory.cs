using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace YobaLog.Tests.Compat;

/// <summary>
/// WebApplicationFactory variant that binds a real Kestrel on an ephemeral port — needed when the
/// client under test is an external process (bun-running JS, another language runtime, …) that
/// can't use the in-memory TestServer handler.
/// </summary>
public sealed class KestrelWebApplicationFactory : WebApplicationFactory<Program>
{
	public string BaseUrl { get; private set; } = "";

	protected override IHost CreateHost(IHostBuilder builder)
	{
		// Re-register Kestrel; UseTestServer was applied upstream by WebApplicationFactory,
		// this overrides IServer back to Kestrel + binds a free port.
		builder.ConfigureWebHost(webHost =>
		{
			webHost.UseKestrel();
			webHost.UseUrls("http://127.0.0.1:0");
		});

		var host = base.CreateHost(builder);
		var server = host.Services.GetRequiredService<IServer>();
		BaseUrl = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
			?? throw new InvalidOperationException("Kestrel did not expose a server address");
		return host;
	}
}
