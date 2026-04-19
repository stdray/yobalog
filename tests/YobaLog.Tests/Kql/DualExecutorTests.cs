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
	[InlineData("events | where Level == 4")]
	[InlineData("events | where Level != 4")]
	[InlineData("events | where Level >= 3")]
	[InlineData("events | where Level > 3")]
	[InlineData("events | where Level <= 2")]
	[InlineData("events | where Level < 2")]
	[InlineData("events | where LevelName == 'Error'")]
	[InlineData("events | where LevelName != 'Information'")]
	[InlineData("events | where TraceId == 't1'")]
	[InlineData("events | where TraceId != 't2'")]
	[InlineData("events | where Message == 'boom'")]
	[InlineData("events | where Message != 'boom'")]
	[InlineData("events | where Id == 3")]
	[InlineData("events | where Id != 3")]
	[InlineData("events | where Id > 3")]
	[InlineData("events | where Id >= 3")]
	[InlineData("events | where Id < 3")]
	[InlineData("events | where Id <= 3")]
	public async Task ScalarComparisons_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Message has 'boom'")]
	[InlineData("events | where Message has 'hello'")]
	[InlineData("events | where Message has 'Earth'")]
	[InlineData("events | where Message has 'unknown-word'")]
	public async Task Has_WordBoundary_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Message contains 'boom'")]
	[InlineData("events | where Message contains 'BOOM'")]
	[InlineData("events | where Message contains 'earth'")]
	[InlineData("events | where Message contains 'no-such-thing'")]
	public async Task Contains_CaseInsensitive_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Level >= 4 and TraceId == 't2'")]
	[InlineData("events | where Level == 4 or Level == 3")]
	[InlineData("events | where Id > 1 and Id <= 4")]
	[InlineData("events | where not(Level == 4)")]
	[InlineData("events | where LevelName == 'Information' and Message contains 'hello'")]
	public async Task LogicalCombinators_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Fact]
	public async Task EmptyResult_BothSidesEmpty()
	{
		await DualExecutor.AssertSameAsync("events | where Level == 5", Dataset);
	}

	[Theory]
	[InlineData("events | order by Id")]
	[InlineData("events | order by Id asc")]
	[InlineData("events | order by Id desc")]
	[InlineData("events | order by Level asc, Id desc")]
	[InlineData("events | order by Level desc, Id asc")]
	[InlineData("events | where Level >= 3 | order by Id")]
	public async Task OrderBy_PreservesOrdering(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset, ordered: true);
	}
}
