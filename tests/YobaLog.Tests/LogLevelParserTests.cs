namespace YobaLog.Tests;

public sealed class LogLevelParserTests
{
	[Theory]
	[InlineData("Verbose", LogLevel.Verbose)]
	[InlineData("verbose", LogLevel.Verbose)]
	[InlineData("VERBOSE", LogLevel.Verbose)]
	[InlineData("Trace", LogLevel.Verbose)]
	[InlineData("Debug", LogLevel.Debug)]
	[InlineData("Information", LogLevel.Information)]
	[InlineData("Info", LogLevel.Information)]
	[InlineData("Warning", LogLevel.Warning)]
	[InlineData("Warn", LogLevel.Warning)]
	[InlineData("Error", LogLevel.Error)]
	[InlineData("Fatal", LogLevel.Fatal)]
	[InlineData("Critical", LogLevel.Fatal)]
	public void Parse_KnownLevel_Returns(string input, LogLevel expected)
	{
		LogLevelParser.Parse(input).Should().Be(expected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("Bogus")]
	[InlineData("info ")]
	public void Parse_Invalid_ReturnsNull(string? input)
	{
		LogLevelParser.Parse(input).Should().BeNull();
	}

	[Fact]
	public void TryParse_Valid_True()
	{
		var ok = LogLevelParser.TryParse("Error", out var l);
		ok.Should().BeTrue();
		l.Should().Be(LogLevel.Error);
	}

	[Fact]
	public void TryParse_Invalid_False()
	{
		var ok = LogLevelParser.TryParse("xxx", out _);
		ok.Should().BeFalse();
	}
}
