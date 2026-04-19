using System.Text;
using YobaLog.Core.Ingestion;

namespace YobaLog.Tests;

public sealed class CleFParserTests
{
	readonly CleFParser _parser = new();

	[Fact]
	public void ParseLine_MinimalValid_Succeeds()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z","@m":"hello"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Timestamp.Should().Be(new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero));
		r.Event.Message.Should().Be("hello");
		r.Event.Level.Should().Be(LogLevel.Information);
	}

	[Fact]
	public void ParseLine_MissingTimestamp_Error()
	{
		var r = _parser.ParseLine("""{"@m":"hi"}""", 1);
		r.IsSuccess.Should().BeFalse();
		r.Error!.Kind.Should().Be(CleFErrorKind.MissingTimestamp);
	}

	[Fact]
	public void ParseLine_MalformedJson_Error()
	{
		var r = _parser.ParseLine("not json", 1);
		r.Error!.Kind.Should().Be(CleFErrorKind.MalformedJson);
	}

	[Theory]
	[InlineData("not-a-date")]
	[InlineData("2026-13-45T00:00:00Z")]
	public void ParseLine_InvalidTimestamp_Error(string ts)
	{
		var json = $$"""{"@t":"{{ts}}"}""";
		var r = _parser.ParseLine(json, 1);
		r.Error!.Kind.Should().Be(CleFErrorKind.InvalidTimestamp);
	}

	[Theory]
	[InlineData("Verbose", LogLevel.Verbose)]
	[InlineData("Trace", LogLevel.Verbose)]
	[InlineData("Debug", LogLevel.Debug)]
	[InlineData("Information", LogLevel.Information)]
	[InlineData("Info", LogLevel.Information)]
	[InlineData("Warning", LogLevel.Warning)]
	[InlineData("Warn", LogLevel.Warning)]
	[InlineData("Error", LogLevel.Error)]
	[InlineData("Fatal", LogLevel.Fatal)]
	[InlineData("Critical", LogLevel.Fatal)]
	[InlineData("ERROR", LogLevel.Error)]
	[InlineData("warn", LogLevel.Warning)]
	public void ParseLine_Level_ParsesCorrectly(string str, LogLevel expected)
	{
		var json = $$"""{"@t":"2026-04-19T10:00:00Z","@l":"{{str}}"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Level.Should().Be(expected);
	}

	[Fact]
	public void ParseLine_UnknownLevel_Error()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z","@l":"Bogus"}""";
		var r = _parser.ParseLine(json, 1);
		r.Error!.Kind.Should().Be(CleFErrorKind.InvalidLevel);
	}

	[Fact]
	public void ParseLine_LevelNotString_Error()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z","@l":42}""";
		var r = _parser.ParseLine(json, 1);
		r.Error!.Kind.Should().Be(CleFErrorKind.InvalidLevel);
	}

	[Fact]
	public void ParseLine_AllFields_Populated()
	{
		var json = """
			{"@t":"2026-04-19T10:00:00Z","@l":"Error","@mt":"User {User} failed","@m":"User alice failed","@x":"exc","@i":42,"@tr":"trace","@sp":"span","User":"alice","Count":5}
			""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		var e = r.Event!;
		e.Level.Should().Be(LogLevel.Error);
		e.MessageTemplate.Should().Be("User {User} failed");
		e.Message.Should().Be("User alice failed");
		e.Exception.Should().Be("exc");
		e.EventId.Should().Be(42);
		e.TraceId.Should().Be("trace");
		e.SpanId.Should().Be("span");
		e.Properties.Should().ContainKey("User");
		e.Properties.Should().ContainKey("Count");
		e.Properties["User"].GetString().Should().Be("alice");
		e.Properties["Count"].GetInt32().Should().Be(5);
	}

	[Fact]
	public void ParseLine_AtAtEscape_LiteralAtProperty()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z","@@weird":"x"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Properties.Should().ContainKey("@weird");
		r.Event.Properties["@weird"].GetString().Should().Be("x");
	}

	[Fact]
	public void ParseLine_UnknownAtField_Skipped()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z","@unknown":"ignored","name":"kept"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Properties.Should().NotContainKey("@unknown");
		r.Event.Properties.Should().NotContainKey("unknown");
		r.Event.Properties.Should().ContainKey("name");
	}

	[Fact]
	public void ParseLine_MessageFallback_UsesTemplate()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z","@mt":"template"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Message.Should().Be("template");
		r.Event.MessageTemplate.Should().Be("template");
	}

	[Fact]
	public void ParseLine_NoMessageFields_EmptyStrings()
	{
		var json = """{"@t":"2026-04-19T10:00:00Z"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Message.Should().BeEmpty();
		r.Event.MessageTemplate.Should().BeEmpty();
	}

	[Fact]
	public void ParseLine_TimestampWithOffset_NormalizedToUtc()
	{
		var json = """{"@t":"2026-04-19T13:00:00+03:00","@m":"x"}""";
		var r = _parser.ParseLine(json, 1);
		r.IsSuccess.Should().BeTrue();
		r.Event!.Timestamp.Should().Be(new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero));
	}

	[Fact]
	public void ParseLine_RootIsArray_Error()
	{
		var r = _parser.ParseLine("[]", 1);
		r.IsSuccess.Should().BeFalse();
		r.Error!.Kind.Should().Be(CleFErrorKind.MalformedJson);
	}

	[Fact]
	public void ParseLine_EmptyString_Error()
	{
		var r = _parser.ParseLine("", 1);
		r.Error!.Kind.Should().Be(CleFErrorKind.MalformedJson);
	}

	[Fact]
	public void ParseLine_TrackingLineNumber()
	{
		var r = _parser.ParseLine("""{"@t":"2026-04-19T10:00:00Z"}""", 42);
		r.LineNumber.Should().Be(42);
	}

	[Fact]
	public async Task ParseAsync_MultipleLines_AllParsed()
	{
		var ndjson =
			"{\"@t\":\"2026-04-19T10:00:00Z\",\"@m\":\"a\"}\n" +
			"{\"@t\":\"2026-04-19T10:00:01Z\",\"@m\":\"b\"}\n" +
			"{\"@t\":\"2026-04-19T10:00:02Z\",\"@m\":\"c\"}\n";
		using var ms = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));

		var results = new List<CleFLineResult>();
		await foreach (var r in _parser.ParseAsync(ms, CancellationToken.None))
			results.Add(r);

		results.Should().HaveCount(3);
		results.Should().AllSatisfy(r => r.IsSuccess.Should().BeTrue());
		results[0].Event!.Message.Should().Be("a");
		results[2].Event!.Message.Should().Be("c");
	}

	[Fact]
	public async Task ParseAsync_TolerantToBadLines_ProducesErrorsNotCrash()
	{
		var ndjson =
			"{\"@t\":\"2026-04-19T10:00:00Z\",\"@m\":\"a\"}\n" +
			"not-json\n" +
			"{\"@t\":\"2026-04-19T10:00:02Z\",\"@m\":\"c\"}\n";
		using var ms = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));

		var results = new List<CleFLineResult>();
		await foreach (var r in _parser.ParseAsync(ms, CancellationToken.None))
			results.Add(r);

		results.Should().HaveCount(3);
		results[0].IsSuccess.Should().BeTrue();
		results[1].IsSuccess.Should().BeFalse();
		results[1].Error!.Kind.Should().Be(CleFErrorKind.MalformedJson);
		results[1].LineNumber.Should().Be(2);
		results[2].IsSuccess.Should().BeTrue();
	}

	[Fact]
	public async Task ParseAsync_EmptyLines_Skipped()
	{
		var ndjson =
			"{\"@t\":\"2026-04-19T10:00:00Z\",\"@m\":\"a\"}\n" +
			"\n" +
			"   \n" +
			"{\"@t\":\"2026-04-19T10:00:01Z\",\"@m\":\"b\"}\n";
		using var ms = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));

		var results = new List<CleFLineResult>();
		await foreach (var r in _parser.ParseAsync(ms, CancellationToken.None))
			results.Add(r);

		results.Should().HaveCount(2);
		results[0].LineNumber.Should().Be(1);
		results[1].LineNumber.Should().Be(4);
	}

	[Fact]
	public async Task ParseAsync_LeavesStreamOpen()
	{
		var ndjson = "{\"@t\":\"2026-04-19T10:00:00Z\"}\n";
		using var ms = new MemoryStream(Encoding.UTF8.GetBytes(ndjson));

		await foreach (var _ in _parser.ParseAsync(ms, CancellationToken.None)) { }

		ms.CanRead.Should().BeTrue();
	}
}
