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

	[Fact]
	public async Task Project_NarrowsColumns()
	{
		var ast = KustoCode.Parse("LogEvents | project Id, Message");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Id", "Message");
		result.Columns.Should().HaveCount(2);

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);

		rows.Should().HaveCount(2);
		rows[0].Should().HaveCount(2);
		rows[0][0].Should().Be(1L);
		rows[0][1].Should().Be("hello");
		rows[1][0].Should().Be(2L);
		rows[1][1].Should().Be("boom");
	}

	[Fact]
	public async Task Project_AliasRenamesColumn()
	{
		var ast = KustoCode.Parse("LogEvents | project EventId = Id, Text = Message");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("EventId", "Text");

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows[0][0].Should().Be(1L);
		rows[0][1].Should().Be("hello");
	}

	[Fact]
	public async Task Where_Then_Project_FilterFirstInSql()
	{
		var ast = KustoCode.Parse("LogEvents | where Level == 4 | project Id, Message");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Id", "Message");
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Should().HaveCount(1);
		rows[0][1].Should().Be("boom");
	}

	[Fact]
	public void Project_UnknownColumn_Throws()
	{
		var ast = KustoCode.Parse("LogEvents | project Bogus");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Bogus*");
	}

	[Fact]
	public void Project_ComputedExpression_ThrowsUnsupported()
	{
		var ast = KustoCode.Parse("LogEvents | project Doubled = Id + Id");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>();
	}
}
