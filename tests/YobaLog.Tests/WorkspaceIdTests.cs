namespace YobaLog.Tests;

public sealed class WorkspaceIdTests
{
	[Theory]
	[InlineData("ab")]
	[InlineData("acme-prod")]
	[InlineData("yobapub")]
	[InlineData("1abc")]
	[InlineData("abc123-xyz")]
	public void Parse_ValidUserSlug_Succeeds(string input)
	{
		var id = WorkspaceId.Parse(input);
		id.Value.Should().Be(input);
		id.IsValid.Should().BeTrue();
		id.IsSystem.Should().BeFalse();
	}

	[Theory]
	[InlineData("$system")]
	[InlineData("$audit")]
	[InlineData("$internal-metrics")]
	public void Parse_ValidSystemSlug_Succeeds(string input)
	{
		var id = WorkspaceId.Parse(input);
		id.Value.Should().Be(input);
		id.IsSystem.Should().BeTrue();
	}

	[Theory]
	[InlineData("")]
	[InlineData("a")]
	[InlineData("-abc")]
	[InlineData("ABC")]
	[InlineData("acme_prod")]
	[InlineData("acme prod")]
	[InlineData("$")]
	[InlineData("$123")]
	[InlineData("abc/def")]
	public void Parse_InvalidSlug_Throws(string input)
	{
		var act = () => WorkspaceId.Parse(input);
		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Parse_Null_Throws()
	{
		var act = () => WorkspaceId.Parse(null!);
		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void TryParse_Valid_ReturnsTrue()
	{
		var ok = WorkspaceId.TryParse("my-workspace", out var id);
		ok.Should().BeTrue();
		id.Value.Should().Be("my-workspace");
	}

	[Fact]
	public void TryParse_Invalid_ReturnsFalse()
	{
		var ok = WorkspaceId.TryParse("INVALID", out var id);
		ok.Should().BeFalse();
		id.IsValid.Should().BeFalse();
	}

	[Fact]
	public void TryParse_Null_ReturnsFalse()
	{
		var ok = WorkspaceId.TryParse(null, out var id);
		ok.Should().BeFalse();
		id.IsValid.Should().BeFalse();
	}

	[Fact]
	public void SystemConstant_Matches()
	{
		WorkspaceId.System.Value.Should().Be("$system");
		WorkspaceId.System.IsSystem.Should().BeTrue();
	}

	[Fact]
	public void Default_IsInvalid()
	{
		var id = default(WorkspaceId);
		id.IsValid.Should().BeFalse();
		id.IsSystem.Should().BeFalse();
	}

	[Fact]
	public void Equality_ByValue()
	{
		var a = WorkspaceId.Parse("acme");
		var b = WorkspaceId.Parse("acme");
		var c = WorkspaceId.Parse("other");
		a.Should().Be(b);
		a.Should().NotBe(c);
	}

	[Fact]
	public void Length_Boundaries()
	{
		var min = new string('a', 2);
		var max = new string('a', 40);
		var tooLong = new string('a', 41);
		WorkspaceId.TryParse(min, out _).Should().BeTrue();
		WorkspaceId.TryParse(max, out _).Should().BeTrue();
		WorkspaceId.TryParse(tooLong, out _).Should().BeFalse();
	}
}
