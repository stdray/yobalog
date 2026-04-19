namespace YobaLog.Tests.Kql;

public sealed class DualExecutorTests
{
	static readonly IReadOnlyList<TestEvent> Dataset =
	[
		new(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "hello", TraceId: "t1"),
		new(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "boom", TraceId: "t2"),
		new(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "meh", TraceId: "t1"),
		new(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Error", "crash"),
		new(5, new DateTime(2026, 4, 19, 10, 4, 0, DateTimeKind.Utc), "Debug", "starting", TraceId: "t3"),
	];

	[Theory]
	[InlineData("LogEvents | where Level == 'Error'")]
	[InlineData("LogEvents | where Level == 'Information'")]
	[InlineData("LogEvents | where Level == 'Debug'")]
	[InlineData("LogEvents | where TraceId == 't1'")]
	[InlineData("LogEvents | where TraceId == 't2'")]
	[InlineData("LogEvents | where Message == 'boom'")]
	[InlineData("LogEvents | where Message == 'nope'")]
	public async Task WhereEquality_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Fact]
	public async Task WhereMatchingNothing_BothReturnEmpty()
	{
		await DualExecutor.AssertSameAsync("LogEvents | where Level == 'Fatal'", Dataset);
	}
}
