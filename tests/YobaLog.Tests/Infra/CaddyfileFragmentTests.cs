namespace YobaLog.Tests.Infra;

// Guard for doc/spec.md §11 SSE-streaming contract: `flush_interval -1` in the fragment's
// reverse_proxy block is load-bearing — without it Caddy buffers upstream response bodies and
// `/api/ws/{id}/tail` events arrive in batches instead of streaming. Cheap grep here replaces
// the full Caddy-in-testcontainers E2E that was considered too heavy. If the line silently
// gets dropped on a future rewrite, this test catches it before deploy.
public sealed class CaddyfileFragmentTests
{
	static string FindFragment()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "infra", "Caddyfile.fragment");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("infra/Caddyfile.fragment not found walking up from test bin");
	}

	[Fact]
	public void Fragment_Has_FlushInterval_Minus_One_For_SSE()
	{
		var text = File.ReadAllText(FindFragment());
		text.Should().Contain("flush_interval -1",
			"SSE live-tail relies on Caddy not buffering the reverse-proxy response body — see doc/spec.md §11");
	}

	[Fact]
	public void Fragment_Reverse_Proxies_To_Port_8082()
	{
		var text = File.ReadAllText(FindFragment());
		text.Should().Contain("127.0.0.1:8082",
			"yobalog's loopback port is 8082 per the host-port convention in doc/spec.md §11");
	}

	[Fact]
	public void Fragment_Host_Block_Is_Yobalog_Domain()
	{
		var text = File.ReadAllText(FindFragment());
		text.Should().Contain("yobalog.3po.su",
			"fragment targets yobalog's production host; do not paste yobaconf's fragment by accident");
	}
}
