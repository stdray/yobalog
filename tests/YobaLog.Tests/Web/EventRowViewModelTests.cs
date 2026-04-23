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
	public void ToJson_MinimalFields()
	{
		var vm = Mk(mt: "hello", m: "hello");
		var json = JsonDocument.Parse(vm.ToJson()).RootElement;

		json.GetProperty("timestamp").GetString().Should().Be("2026-04-23T08:30:15.250Z");
		json.GetProperty("level").GetString().Should().Be("Information");
		json.GetProperty("messageTemplate").GetString().Should().Be("hello");
		// `message` omitted when identical to the template (renderer can rebuild it).
		json.TryGetProperty("message", out _).Should().BeFalse();
		json.TryGetProperty("exception", out _).Should().BeFalse();
		json.TryGetProperty("traceId", out _).Should().BeFalse();
		// `properties` omitted when the dictionary is empty.
		json.TryGetProperty("properties", out _).Should().BeFalse();
	}

	[Fact]
	public void ToJson_AllOptionalFields()
	{
		var vm = Mk(
			mt: "login {User}",
			m: "login 'ada'",
			ex: "System.Exception: boom\n   at Foo()",
			tr: "abc123",
			sp: "def456",
			eventId: 7);

		var json = JsonDocument.Parse(vm.ToJson()).RootElement;
		json.GetProperty("messageTemplate").GetString().Should().Be("login {User}");
		json.GetProperty("message").GetString().Should().Be("login 'ada'");
		json.GetProperty("exception").GetString().Should().Contain("boom");
		json.GetProperty("traceId").GetString().Should().Be("abc123");
		json.GetProperty("spanId").GetString().Should().Be("def456");
		json.GetProperty("eventId").GetInt32().Should().Be(7);
	}

	[Fact]
	public void ToJson_PropertiesNestedUnderProperties()
	{
		// Nested to avoid collisions with top-level keys (User, Count, Level, …).
		var props = ImmutableDictionary<string, JsonElement>.Empty
			.Add("User", Str("ada"))
			.Add("Count", Num(3));
		var vm = Mk(props: props);

		var json = JsonDocument.Parse(vm.ToJson()).RootElement;
		var nested = json.GetProperty("properties");
		nested.GetProperty("User").GetString().Should().Be("ada");
		nested.GetProperty("Count").GetInt32().Should().Be(3);
		// Top-level stays clean.
		json.TryGetProperty("User", out _).Should().BeFalse();
	}

	[Fact]
	public void ToJson_SpecialCharsInStrings_NotOverEscaped()
	{
		var vm = Mk(mt: "msg with <angle> & \"quote\"", m: "msg with <angle> & \"quote\"");
		var raw = vm.ToJson();

		// UnsafeRelaxedJsonEscaping keeps < > & readable; " is still JSON-escaped as \".
		raw.Should().Contain("<angle>");
		raw.Should().Contain("&");
		raw.Should().Contain("\\\"quote\\\"");

		// Parses as valid JSON regardless.
		using var doc = JsonDocument.Parse(raw);
		doc.RootElement.GetProperty("messageTemplate").GetString().Should().Be("msg with <angle> & \"quote\"");
	}

	[Fact]
	public void ToJson_IsIndented()
	{
		// Indented output helps when the copied payload is pasted into a ticket / chat.
		var vm = Mk();
		vm.ToJson().Should().Contain("\n");
	}
}
