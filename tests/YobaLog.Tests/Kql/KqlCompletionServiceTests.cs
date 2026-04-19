using YobaLog.Core.Kql;

namespace YobaLog.Tests.Kql;

public sealed class KqlCompletionServiceTests
{
	readonly KqlCompletionService _svc = new();

	[Fact]
	public void ColumnPrefix_ReturnsMatchingColumns()
	{
		const string q = "events | where Le";
		var result = _svc.Complete(q, q.Length);

		result.Items.Should().Contain(i => i.DisplayText == "Level");
		result.Items.Should().Contain(i => i.DisplayText == "LevelName");
		result.Items.Should().AllSatisfy(i => i.DisplayText.StartsWith("Le", StringComparison.Ordinal));
	}

	[Fact]
	public void TableName_ReturnsEvents()
	{
		const string q = "ev";
		var result = _svc.Complete(q, q.Length);

		result.Items.Should().Contain(i => i.DisplayText == "events");
	}

	[Fact]
	public void EditRange_CoversPrefix()
	{
		const string q = "events | where Lev";
		var result = _svc.Complete(q, q.Length);

		var prefix = q.Substring(result.EditStart, result.EditLength);
		prefix.Should().Be("Lev");
	}

	[Fact]
	public void NoItems_ReturnsEmpty()
	{
		const string q = "events | where ZzzzzNothingMatches";
		var result = _svc.Complete(q, q.Length);
		result.Items.Should().BeEmpty();
	}

	[Fact]
	public void PositionOutOfRange_Clamped()
	{
		const string q = "events";
		var result = _svc.Complete(q, q.Length * 10);
		result.Should().NotBeNull();
	}

	[Fact]
	public void MaxItems_RespectsCap()
	{
		var result = _svc.Complete("", 0);
		result.Items.Should().HaveCountLessThanOrEqualTo(KqlCompletionService.MaxItems);
	}
}
