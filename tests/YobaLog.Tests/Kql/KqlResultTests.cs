using Kusto.Language;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Kql;

public sealed class KqlResultTests
{
	readonly KqlTransformer _transformer = new();

	static readonly EventRecord[] Rows =
	[
		new() { Id = 1, TimestampMs = 100, Level = (int)LogLevel.Information, Message = "hello", TraceId = "t1" },
		new() { Id = 2, TimestampMs = 200, Level = (int)LogLevel.Error, Message = "boom", TraceId = "t2" },
	];

	[Fact]
	public void EventRecordColumns_HaveExpectedSchema()
	{
		KqlTransformer.EventRecordColumns.Select(c => c.Name).Should().ContainInOrder(
			"Id", "Timestamp", "Level", "MessageTemplate", "Message",
			"Exception", "TraceId", "SpanId", "EventId", "PropertiesJson");
		KqlTransformer.EventRecordColumns.Single(c => c.Name == "Timestamp").ClrType.Should().Be<DateTimeOffset>();
		KqlTransformer.EventRecordColumns.Single(c => c.Name == "Level").ClrType.Should().Be<int>();
	}

	[Fact]
	public async Task Execute_Where_ReturnsFilteredRowsAsObjectArray()
	{
		var ast = KustoCode.Parse("LogEvents | where Level == 4");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows)
			rows.Add(r);

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(2L); // Id
		rows[0][4].Should().Be("boom"); // Message
	}

	[Fact]
	public async Task Execute_TimestampColumn_IsDateTimeOffset()
	{
		var ast = KustoCode.Parse("LogEvents | where Id == 1");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		await foreach (var r in result.Rows)
		{
			r[1].Should().BeOfType<DateTimeOffset>();
			((DateTimeOffset)r[1]!).ToUnixTimeMilliseconds().Should().Be(100);
		}
	}

	[Fact]
	public void Execute_Unsupported_ThrowsEagerly()
	{
		var ast = KustoCode.Parse("LogEvents | summarize count()");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*SummarizeOperator*");
	}
}
