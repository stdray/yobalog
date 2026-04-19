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
		var ast = KustoCode.Parse("events | where Level == 4");
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
		var ast = KustoCode.Parse("events | where Id == 1");
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
		var ast = KustoCode.Parse("events | distinct Level");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>();
	}

	[Fact]
	public async Task Project_NarrowsColumns()
	{
		var ast = KustoCode.Parse("events | project Id, Message");
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
		var ast = KustoCode.Parse("events | project EventId = Id, Text = Message");
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
		var ast = KustoCode.Parse("events | where Level == 4 | project Id, Message");
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
		var ast = KustoCode.Parse("events | project Bogus");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Bogus*");
	}

	[Fact]
	public void Project_ComputedExpression_ThrowsUnsupported()
	{
		var ast = KustoCode.Parse("events | project Doubled = Id + Id");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>();
	}

	[Fact]
	public async Task Count_ReturnsScalar()
	{
		var ast = KustoCode.Parse("events | count");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Should().HaveCount(1);
		result.Columns[0].Name.Should().Be("Count");
		result.Columns[0].ClrType.Should().Be<long>();

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(2L);
	}

	[Fact]
	public async Task Where_Then_Count_FiltersFirst()
	{
		var ast = KustoCode.Parse("events | where Level == 4 | count");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows[0][0].Should().Be(1L);
	}

	static readonly EventRecord[] SummarizeRows =
	[
		new() { Id = 1, Level = (int)LogLevel.Error, Message = "a", TraceId = "t1" },
		new() { Id = 2, Level = (int)LogLevel.Error, Message = "b", TraceId = "t1" },
		new() { Id = 3, Level = (int)LogLevel.Warning, Message = "c", TraceId = "t2" },
		new() { Id = 4, Level = (int)LogLevel.Information, Message = "d", TraceId = "t1" },
		new() { Id = 5, Level = (int)LogLevel.Error, Message = "e", TraceId = "t2" },
	];

	[Fact]
	public async Task Summarize_CountByColumn_GroupsCorrectly()
	{
		var ast = KustoCode.Parse("events | summarize count() by Level");
		var result = _transformer.Execute(SummarizeRows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Level", "count_");

		var rows = new List<(int Level, long Count)>();
		await foreach (var r in result.Rows)
			rows.Add(((int)r[0]!, (long)r[1]!));

		rows.Should().BeEquivalentTo(new (int, long)[]
		{
			((int)LogLevel.Error, 3),
			((int)LogLevel.Warning, 1),
			((int)LogLevel.Information, 1),
		});
	}

	[Fact]
	public async Task Summarize_CountWithAlias_UsesGivenName()
	{
		var ast = KustoCode.Parse("events | summarize n = count() by Level");
		var result = _transformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Level", "n");

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Sum(r => (long)r[1]!).Should().Be(5);
	}

	[Fact]
	public async Task Summarize_CountByMultipleColumns()
	{
		var ast = KustoCode.Parse("events | summarize count() by Level, TraceId");
		var result = _transformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Level", "TraceId", "count_");

		var rows = new List<(int Level, string? TraceId, long Count)>();
		await foreach (var r in result.Rows)
			rows.Add(((int)r[0]!, (string?)r[1], (long)r[2]!));

		rows.Should().Contain(((int)LogLevel.Error, "t1", 2L));
		rows.Should().Contain(((int)LogLevel.Error, "t2", 1L));
		rows.Should().Contain(((int)LogLevel.Warning, "t2", 1L));
		rows.Should().Contain(((int)LogLevel.Information, "t1", 1L));
	}

	[Fact]
	public async Task Summarize_CountWithoutBy_SingleRow()
	{
		var ast = KustoCode.Parse("events | summarize count()");
		var result = _transformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("count_");

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(5L);
	}

	[Fact]
	public void Summarize_UnsupportedAggregate_Throws()
	{
		var ast = KustoCode.Parse("events | summarize avg(Level)");
		var act = () => _transformer.Execute(SummarizeRows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*avg*");
	}

	[Fact]
	public async Task Extend_AppendsAliasColumn()
	{
		var ast = KustoCode.Parse("events | extend Copy = Level");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Should().HaveCount(KqlTransformer.EventRecordColumns.Count + 1);
		result.Columns[^1].Name.Should().Be("Copy");
		result.Columns[^1].ClrType.Should().Be<int>();

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows[0][^1].Should().Be((int)LogLevel.Information);
		rows[1][^1].Should().Be((int)LogLevel.Error);
	}

	[Fact]
	public async Task Extend_Then_Project_Works()
	{
		var ast = KustoCode.Parse("events | extend Copy = Level | project Id, Copy");
		var result = _transformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Id", "Copy");
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows[0][1].Should().Be((int)LogLevel.Information);
	}

	[Fact]
	public void Extend_UnknownColumn_Throws()
	{
		var ast = KustoCode.Parse("events | extend X = Nope");
		var act = () => _transformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Nope*");
	}
}
