using System.Text;
using BenchmarkDotNet.Attributes;
using YobaLog.Core.Ingestion;

namespace YobaLog.Benchmarks;

[MemoryDiagnoser]
public class CleFParserBenchmark
{
	readonly CleFParser _parser = new();

	byte[] _payload = [];

	[Params(100, 1_000, 10_000)]
	public int BatchSize { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		var sb = new StringBuilder();
		for (var i = 0; i < BatchSize; i++)
		{
			sb.Append(System.Globalization.CultureInfo.InvariantCulture,
				$"{{\"@t\":\"2026-04-19T10:{i % 60:D2}:00Z\",\"@l\":\"{(i % 3 == 0 ? "Error" : "Information")}\",\"@m\":\"event {i} happened\",\"@tr\":\"trace-{i}\",\"User\":\"alice\",\"Count\":{i}}}\n");
		}
		_payload = Encoding.UTF8.GetBytes(sb.ToString());
	}

	[Benchmark]
	public async Task<int> ParseStreamAsync()
	{
		using var ms = new MemoryStream(_payload);
		var count = 0;
		await foreach (var line in _parser.ParseAsync(ms, CancellationToken.None))
		{
			if (line.IsSuccess)
				count++;
		}
		return count;
	}
}
