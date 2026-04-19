using Kusto.Language;
using YobaLog.Core.Kql;
using YobaLog.Core.Storage.Sqlite;

namespace YobaLog.Tests.Kql;

public sealed class KqlTransformerTests
{
	readonly KqlTransformer _transformer = new();

	static readonly EventRecord[] Rows =
	[
		new() { Id = 1, TimestampMs = 100, Level = (int)LogLevel.Information, Message = "hello", TraceId = "t1" },
		new() { Id = 2, TimestampMs = 200, Level = (int)LogLevel.Error, Message = "boom", TraceId = "t2" },
		new() { Id = 3, TimestampMs = 300, Level = (int)LogLevel.Warning, Message = "meh", TraceId = "t1" },
		new() { Id = 4, TimestampMs = 400, Level = (int)LogLevel.Error, Message = "crash", TraceId = null },
	];

	static IQueryable<EventRecord> Src() => Rows.AsQueryable();

	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	[Fact]
	public void Where_LevelEquals_FiltersByInt()
	{
		var ast = Parse("LogEvents | where Level == 'Error'");
		var result = _transformer.Apply(Src(), ast).ToList();

		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 4L]);
	}

	[Fact]
	public void Where_TraceIdEquals_Filters()
	{
		var ast = Parse("LogEvents | where TraceId == 't1'");
		var result = _transformer.Apply(Src(), ast).ToList();

		result.Select(r => r.Id).Should().BeEquivalentTo([1L, 3L]);
	}

	[Fact]
	public void Where_MessageEquals_Filters()
	{
		var ast = Parse("LogEvents | where Message == 'boom'");
		var result = _transformer.Apply(Src(), ast).ToList();

		result.Single().Id.Should().Be(2);
	}

	[Fact]
	public void Take_LimitsRows()
	{
		var ast = Parse("LogEvents | take 2");
		var result = _transformer.Apply(Src(), ast).ToList();

		result.Should().HaveCount(2);
	}

	[Fact]
	public void Where_Then_Take_Composes()
	{
		var ast = Parse("LogEvents | where Level == 'Error' | take 1");
		var result = _transformer.Apply(Src(), ast).ToList();

		result.Should().HaveCount(1);
		result.Single().Level.Should().Be((int)LogLevel.Error);
	}

	[Fact]
	public void UnknownTable_Throws()
	{
		var ast = Parse("WrongTable | where Level == 'Error'");
		var act = () => _transformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*WrongTable*");
	}

	[Fact]
	public void UnknownOperator_Throws()
	{
		var ast = Parse("LogEvents | summarize count()");
		var act = () => _transformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*not supported*");
	}

	[Fact]
	public void UnknownColumn_Throws()
	{
		var ast = Parse("LogEvents | where Nonexistent == 'x'");
		var act = () => _transformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Nonexistent*");
	}

	[Fact]
	public void UnknownLogLevel_Throws()
	{
		var ast = Parse("LogEvents | where Level == 'Purple'");
		var act = () => _transformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Purple*");
	}

	[Fact]
	public void ParseError_Throws()
	{
		var ast = Parse("LogEvents | where Level ==");
		var act = () => _transformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*parse error*");
	}
}
