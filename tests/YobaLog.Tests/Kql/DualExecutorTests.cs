namespace YobaLog.Tests.Kql;

public sealed class DualExecutorTests
{
	static readonly IReadOnlyList<TestEvent> Dataset =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "hello world", traceId: "t1"),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "boom", traceId: "t2"),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "meh", traceId: "t1"),
		TestEvent.FromName(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Error", "crash on Earth"),
		TestEvent.FromName(5, new DateTime(2026, 4, 19, 10, 4, 0, DateTimeKind.Utc), "Debug", "starting", traceId: "t3"),
		TestEvent.FromName(6, new DateTime(2026, 4, 19, 10, 5, 0, DateTimeKind.Utc), "Information", "BOOM normalized", traceId: "t2"),
	];

	[Theory]
	[InlineData("LogEvents | where Level == 4")]
	[InlineData("LogEvents | where Level != 4")]
	[InlineData("LogEvents | where Level >= 3")]
	[InlineData("LogEvents | where Level > 3")]
	[InlineData("LogEvents | where Level <= 2")]
	[InlineData("LogEvents | where Level < 2")]
	[InlineData("LogEvents | where LevelName == 'Error'")]
	[InlineData("LogEvents | where LevelName != 'Information'")]
	[InlineData("LogEvents | where TraceId == 't1'")]
	[InlineData("LogEvents | where TraceId != 't2'")]
	[InlineData("LogEvents | where Message == 'boom'")]
	[InlineData("LogEvents | where Message != 'boom'")]
	[InlineData("LogEvents | where Id == 3")]
	[InlineData("LogEvents | where Id != 3")]
	[InlineData("LogEvents | where Id > 3")]
	[InlineData("LogEvents | where Id >= 3")]
	[InlineData("LogEvents | where Id < 3")]
	[InlineData("LogEvents | where Id <= 3")]
	public async Task ScalarComparisons_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("LogEvents | where Message contains 'boom'")]
	[InlineData("LogEvents | where Message contains 'BOOM'")]
	[InlineData("LogEvents | where Message contains 'earth'")]
	[InlineData("LogEvents | where Message contains 'no-such-thing'")]
	public async Task Contains_CaseInsensitive_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("LogEvents | where Level >= 4 and TraceId == 't2'")]
	[InlineData("LogEvents | where Level == 4 or Level == 3")]
	[InlineData("LogEvents | where Id > 1 and Id <= 4")]
	[InlineData("LogEvents | where not(Level == 4)")]
	[InlineData("LogEvents | where LevelName == 'Information' and Message contains 'hello'")]
	public async Task LogicalCombinators_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Fact]
	public async Task EmptyResult_BothSidesEmpty()
	{
		await DualExecutor.AssertSameAsync("LogEvents | where Level == 5", Dataset);
	}

	[Theory]
	[InlineData("LogEvents | order by Id")]
	[InlineData("LogEvents | order by Id asc")]
	[InlineData("LogEvents | order by Id desc")]
	[InlineData("LogEvents | order by Level asc, Id desc")]
	[InlineData("LogEvents | order by Level desc, Id asc")]
	[InlineData("LogEvents | where Level >= 3 | order by Id")]
	public async Task OrderBy_PreservesOrdering(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset, ordered: true);
	}
}
