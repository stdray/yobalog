using System.Collections.Immutable;
using System.Text.Json;
using YobaLog.Core;
using YobaLog.Web;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Tests.Web;

public sealed class EventRowViewModelTests
{
	static JsonElement Str(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
	static JsonElement Num(int n) => JsonDocument.Parse(n.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

	static EventRowViewModel Mk(
		string mt = "User {Name} logged in",
		string m = "User 'ada' logged in",
		string? ex = null,
		string? tr = null,
		string? sp = null,
		int? eventId = null,
		ImmutableDictionary<string, JsonElement>? props = null,
		LogLevel level = LogLevel.Information) => new(
			Id: 42,
			Timestamp: DateTimeOffset.Parse("2026-04-23T08:30:15.250Z", System.Globalization.CultureInfo.InvariantCulture),
			Level: level,
			MessageTemplate: mt,
			Message: m,
			Exception: ex,
			TraceId: tr,
			SpanId: sp,
			EventId: eventId,
			Properties: props ?? ImmutableDictionary<string, JsonElement>.Empty,
			IsLive: false);

	[Fact]
	public void ToClefJson_MinimalFields()
	{
		var vm = Mk(mt: "hello", m: "hello");
		var json = JsonDocument.Parse(vm.ToClefJson()).RootElement;

		json.GetProperty("@t").GetString().Should().Be("2026-04-23T08:30:15.250Z");
		json.GetProperty("@l").GetString().Should().Be("Information");
		json.GetProperty("@mt").GetString().Should().Be("hello");
		// @m omitted when identical to @mt (CLEF convention: renderer can rebuild from @mt).
		json.TryGetProperty("@m", out _).Should().BeFalse();
		json.TryGetProperty("@x", out _).Should().BeFalse();
		json.TryGetProperty("@tr", out _).Should().BeFalse();
	}

	[Fact]
	public void ToClefJson_AllOptionalFields()
	{
		var vm = Mk(
			mt: "login {User}",
			m: "login 'ada'",
			ex: "System.Exception: boom\n   at Foo()",
			tr: "abc123",
			sp: "def456",
			eventId: 7);

		var json = JsonDocument.Parse(vm.ToClefJson()).RootElement;
		json.GetProperty("@mt").GetString().Should().Be("login {User}");
		json.GetProperty("@m").GetString().Should().Be("login 'ada'");
		json.GetProperty("@x").GetString().Should().Contain("boom");
		json.GetProperty("@tr").GetString().Should().Be("abc123");
		json.GetProperty("@sp").GetString().Should().Be("def456");
		json.GetProperty("@i").GetInt32().Should().Be(7);
	}

	[Fact]
	public void ToClefJson_PropertiesFlattened()
	{
		var props = ImmutableDictionary<string, JsonElement>.Empty
			.Add("User", Str("ada"))
			.Add("Count", Num(3));
		var vm = Mk(props: props);

		var json = JsonDocument.Parse(vm.ToClefJson()).RootElement;
		json.GetProperty("User").GetString().Should().Be("ada");
		json.GetProperty("Count").GetInt32().Should().Be(3);
	}

	[Fact]
	public void ToClefJson_SpecialCharsInStrings_NotOverEscaped()
	{
		var vm = Mk(mt: "msg with <angle> & \"quote\"", m: "msg with <angle> & \"quote\"");
		var raw = vm.ToClefJson();

		// UnsafeRelaxedJsonEscaping keeps < > & readable; " is still JSON-escaped as \".
		raw.Should().Contain("<angle>");
		raw.Should().Contain("&");
		raw.Should().Contain("\\\"quote\\\"");

		// Parses as valid JSON regardless.
		using var doc = JsonDocument.Parse(raw);
		doc.RootElement.GetProperty("@mt").GetString().Should().Be("msg with <angle> & \"quote\"");
	}
}
