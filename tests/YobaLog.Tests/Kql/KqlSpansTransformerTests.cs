using Kusto.Language;
using YobaLog.Core.Kql;
using YobaLog.Core.Tracing.Sqlite;

namespace YobaLog.Tests.Kql;

// Parallel to KqlTransformerTests but scoped to the spans target. H.3 supports
// filter/sort/limit only; shape-changing ops throw UnsupportedKqlException.
public sealed class KqlSpansTransformerTests
{
	static readonly SpanRecord[] Rows =
	[
		new()
		{
			SpanId = "aaaaaaaaaaaaaaaa",
			TraceId = "aabbccddeeff00112233445566778899",
			ParentSpanId = null,
			Name = "root.request",
			Kind = 2, // Server
			StartUnixNs = 1_700_000_000_000_000_000L,
			EndUnixNs = 1_700_000_000_500_000_000L, // 500ms
			StatusCode = 1, // Ok
		},
		new()
		{
			SpanId = "bbbbbbbbbbbbbbbb",
			TraceId = "aabbccddeeff00112233445566778899",
			ParentSpanId = "aaaaaaaaaaaaaaaa",
			Name = "db.query",
			Kind = 3, // Client
			StartUnixNs = 1_700_000_000_100_000_000L,
			EndUnixNs = 1_700_000_000_200_000_000L, // 100ms
			StatusCode = 1, // Ok
		},
		new()
		{
			SpanId = "cccccccccccccccc",
			TraceId = "aabbccddeeff00112233445566778899",
			ParentSpanId = "aaaaaaaaaaaaaaaa",
			Name = "cache.lookup",
			Kind = 1, // Internal
			StartUnixNs = 1_700_000_000_300_000_000L,
			EndUnixNs = 1_700_000_000_310_000_000L, // 10ms
			StatusCode = 2, // Error
		},
	];

	static IQueryable<SpanRecord> Src() => Rows.AsQueryable();
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	[Fact]
	public void Where_On_TraceId_String_Filters()
	{
		var ast = Parse("spans | where TraceId == 'aabbccddeeff00112233445566778899'");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Should().HaveCount(3);
	}

	[Fact]
	public void Where_On_Status_Int_Filters()
	{
		var ast = Parse("spans | where Status == 2");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["cache.lookup"]);
	}

	[Fact]
	public void Where_On_Kind_Int_Filters()
	{
		var ast = Parse("spans | where Kind == 3"); // Client
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["db.query"]);
	}

	[Fact]
	public void Where_Duration_Greater_Than_Nanoseconds()
	{
		// Duration = End - Start in ns. 200_000_000ns = 200ms.
		var ast = Parse("spans | where Duration > 200000000");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		// Only root.request (500ms) qualifies.
		result.Select(s => s.Name).Should().BeEquivalentTo(["root.request"]);
	}

	[Fact]
	public void Where_Name_Contains_CaseInsensitive()
	{
		var ast = Parse("spans | where Name contains 'db'");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["db.query"]);
	}

	[Fact]
	public void Where_ParentSpanId_Null_Root_Only()
	{
		// Root span has ParentSpanId = null. `where ParentSpanId == ''` or similar isn't
		// directly expressible — tests verify that querying on a concrete parent returns
		// only children.
		var ast = Parse("spans | where ParentSpanId == 'aaaaaaaaaaaaaaaa'");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["db.query", "cache.lookup"]);
	}

	[Fact]
	public void Where_StartTime_GreaterThan_Datetime()
	{
		// 1_700_000_000_200_000_000 ns = 2023-11-14 22:13:20.200 UTC
		var ast = Parse("spans | where StartTime >= datetime(2023-11-14T22:13:20.200Z)");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["cache.lookup"]);
	}

	[Fact]
	public void Order_By_Duration_Descending_ByDefault()
	{
		var ast = Parse("spans | order by Duration");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		// 500ms → 100ms → 10ms
		result.Select(s => s.Name).Should().ContainInOrder("root.request", "db.query", "cache.lookup");
	}

	[Fact]
	public void Order_By_StartTime_Asc()
	{
		var ast = Parse("spans | order by StartTime asc");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().ContainInOrder("root.request", "db.query", "cache.lookup");
	}

	[Fact]
	public void Take_Limits_Result()
	{
		var ast = Parse("spans | order by Duration | take 2");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Should().HaveCount(2);
		result.Select(s => s.Name).Should().ContainInOrder("root.request", "db.query");
	}

	[Fact]
	public void Where_And_Chained()
	{
		var ast = Parse("spans | where Status == 1 and Kind == 3");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["db.query"]);
	}

	[Fact]
	public void Where_Or_Chained()
	{
		var ast = Parse("spans | where Kind == 2 or Status == 2");
		var result = KqlSpansTransformer.Apply(Src(), ast).ToList();
		result.Select(s => s.Name).Should().BeEquivalentTo(["root.request", "cache.lookup"]);
	}

	[Fact]
	public void Unknown_Table_Rejected()
	{
		var ast = Parse("events | where Level == 4");
		var act = () => KqlSpansTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>()
			.WithMessage("*'events'*only 'spans'*");
	}

	[Fact]
	public void Unsupported_Operator_Project_Throws()
	{
		var ast = Parse("spans | project Name");
		var act = () => KqlSpansTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>()
			.WithMessage("*not supported on spans target*");
	}

	[Fact]
	public void Unsupported_Operator_Summarize_Throws()
	{
		var ast = Parse("spans | summarize count() by Kind");
		var act = () => KqlSpansTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>();
	}

	[Fact]
	public void Unknown_Column_Rejected()
	{
		var ast = Parse("spans | where NoSuchColumn == 'x'");
		var act = () => KqlSpansTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>()
			.WithMessage("*'NoSuchColumn'*");
	}
}
