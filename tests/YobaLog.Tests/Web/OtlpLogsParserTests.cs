using System.Collections.Immutable;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using YobaLog.Web.Ingestion;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Tests.Web;

// Unit tests for OtlpLogsParser — verify the decision-log 2026-04-21 mapping table row by row.
// Construct ExportLogsServiceRequest via the generated proto types, serialize to bytes,
// feed to Parse. That exercises the full wire path including protobuf encoding quirks
// (varint / fixed64 / bytes-vs-string oneof branches) without mocking.
public sealed class OtlpLogsParserTests
{
	static byte[] SerializeRequest(ExportLogsServiceRequest request) => request.ToByteArray();

	static ExportLogsServiceRequest Single(LogRecord record, params KeyValue[] resourceAttrs)
	{
		var resource = new Resource();
		foreach (var kv in resourceAttrs) resource.Attributes.Add(kv);
		var scope = new ScopeLogs();
		scope.LogRecords.Add(record);
		var rl = new ResourceLogs { Resource = resource };
		rl.ScopeLogs.Add(scope);
		var req = new ExportLogsServiceRequest();
		req.ResourceLogs.Add(rl);
		return req;
	}

	static LogRecord MakeRecord(
		ulong timeUnixNano = 1_700_000_000_000_000_000UL,
		SeverityNumber severity = SeverityNumber.Info,
		string body = "hello")
	{
		return new LogRecord
		{
			TimeUnixNano = timeUnixNano,
			SeverityNumber = severity,
			Body = new AnyValue { StringValue = body },
		};
	}

	[Fact]
	public void TimeUnixNano_MapsTo_Timestamp_InMilliseconds()
	{
		// 1_700_000_000_000_000_000 ns = 2023-11-14 22:13:20 UTC (Unix ms: 1_700_000_000_000).
		var req = Single(MakeRecord(timeUnixNano: 1_700_000_000_000_000_000UL));
		var result = OtlpLogsParser.Parse(SerializeRequest(req));

		result.IsMalformed.Should().BeFalse();
		result.Candidates.Should().HaveCount(1);
		result.Candidates[0].Timestamp.ToUnixTimeMilliseconds().Should().Be(1_700_000_000_000);
	}

	[Fact]
	public void TimeUnixNano_Zero_FallsBackTo_ObservedTimeUnixNano()
	{
		var record = MakeRecord(timeUnixNano: 0);
		record.ObservedTimeUnixNano = 1_800_000_000_000_000_000UL;

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates.Should().HaveCount(1);
		result.Candidates[0].Timestamp.ToUnixTimeMilliseconds().Should().Be(1_800_000_000_000);
	}

	[Fact]
	public void BothTimestamps_Zero_RejectsRecord()
	{
		var record = MakeRecord(timeUnixNano: 0);
		record.ObservedTimeUnixNano = 0;

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates.Should().BeEmpty();
		result.Errors.Should().Be(1);
	}

	[Theory]
	[InlineData(SeverityNumber.Trace, LogLevel.Verbose)]
	[InlineData(SeverityNumber.Trace4, LogLevel.Verbose)]
	[InlineData(SeverityNumber.Debug, LogLevel.Debug)]
	[InlineData(SeverityNumber.Debug4, LogLevel.Debug)]
	[InlineData(SeverityNumber.Info, LogLevel.Information)]
	[InlineData(SeverityNumber.Info4, LogLevel.Information)]
	[InlineData(SeverityNumber.Warn, LogLevel.Warning)]
	[InlineData(SeverityNumber.Warn4, LogLevel.Warning)]
	[InlineData(SeverityNumber.Error, LogLevel.Error)]
	[InlineData(SeverityNumber.Error4, LogLevel.Error)]
	[InlineData(SeverityNumber.Fatal, LogLevel.Fatal)]
	[InlineData(SeverityNumber.Fatal4, LogLevel.Fatal)]
	[InlineData(SeverityNumber.Unspecified, LogLevel.Information)]
	public void SeverityNumber_MapsTo_LogLevel(SeverityNumber severity, LogLevel expected)
	{
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(MakeRecord(severity: severity))));
		result.Candidates.Should().HaveCount(1);
		result.Candidates[0].Level.Should().Be(expected);
	}

	[Fact]
	public void SeverityText_GoesTo_Properties()
	{
		var record = MakeRecord();
		record.SeverityText = "NOTICE"; // custom level outside the OTel enum ladder
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates.Should().HaveCount(1);
		result.Candidates[0].Properties.Should().ContainKey("severity_text");
		result.Candidates[0].Properties["severity_text"].GetString().Should().Be("NOTICE");
	}

	[Fact]
	public void Body_StringValue_UsedAsMessage()
	{
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(MakeRecord(body: "plain string"))));
		result.Candidates[0].Message.Should().Be("plain string");
	}

	[Fact]
	public void Body_IntValue_IsStringified()
	{
		var record = new LogRecord
		{
			TimeUnixNano = 1_700_000_000_000_000_000UL,
			Body = new AnyValue { IntValue = 42 },
		};
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));
		result.Candidates[0].Message.Should().Be("42");
	}

	[Fact]
	public void Body_BoolValue_IsStringified()
	{
		var record = new LogRecord
		{
			TimeUnixNano = 1_700_000_000_000_000_000UL,
			Body = new AnyValue { BoolValue = true },
		};
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));
		result.Candidates[0].Message.Should().Be("true");
	}

	[Fact]
	public void Body_KvListValue_SerializedAsJson()
	{
		var kvList = new KeyValueList();
		kvList.Values.Add(new KeyValue { Key = "k", Value = new AnyValue { StringValue = "v" } });
		var record = new LogRecord
		{
			TimeUnixNano = 1_700_000_000_000_000_000UL,
			Body = new AnyValue { KvlistValue = kvList },
		};

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));
		result.Candidates[0].Message.Should().Contain("\"k\"").And.Contain("\"v\"");
	}

	[Fact]
	public void Attributes_FlattenedIntoProperties()
	{
		var record = MakeRecord();
		record.Attributes.Add(new KeyValue { Key = "user.id", Value = new AnyValue { StringValue = "42" } });
		record.Attributes.Add(new KeyValue { Key = "count", Value = new AnyValue { IntValue = 7 } });

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates[0].Properties.Should().ContainKey("user.id");
		result.Candidates[0].Properties["user.id"].GetString().Should().Be("42");
		result.Candidates[0].Properties["count"].GetInt32().Should().Be(7);
	}

	[Fact]
	public void ResourceAttributes_Win_OnCollision_With_RecordAttributes()
	{
		// Resource says service.name=yobalog; record tries to override with "not-yobalog".
		// Deployment identity must win — decision-log 2026-04-21 mapping row for resource.attributes.
		var record = MakeRecord();
		record.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "not-yobalog" } });

		var resourceAttr = new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "yobalog" } };
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record, resourceAttr)));

		result.Candidates[0].Properties["service.name"].GetString().Should().Be("yobalog");
	}

	[Fact]
	public void TraceId_And_SpanId_HexEncoded()
	{
		var record = MakeRecord();
		record.TraceId = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
		record.SpanId = ByteString.CopyFrom(Enumerable.Range(0x10, 8).Select(i => (byte)i).ToArray());

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates[0].TraceId.Should().Be("000102030405060708090a0b0c0d0e0f");
		result.Candidates[0].SpanId.Should().Be("1011121314151617");
	}

	[Fact]
	public void AllZero_TraceId_SpanId_Treated_AsAbsent()
	{
		var record = MakeRecord();
		record.TraceId = ByteString.CopyFrom(new byte[16]);
		record.SpanId = ByteString.CopyFrom(new byte[8]);

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates[0].TraceId.Should().BeNull();
		result.Candidates[0].SpanId.Should().BeNull();
	}

	[Fact]
	public void EventName_MapsTo_MessageTemplate()
	{
		var record = MakeRecord(body: "request completed in 42ms");
		record.EventName = "http.request.completed";

		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));

		result.Candidates[0].MessageTemplate.Should().Be("http.request.completed");
		result.Candidates[0].Message.Should().Be("request completed in 42ms");
	}

	[Fact]
	public void EventName_Absent_FallsBackTo_Body_AsTemplate()
	{
		var record = MakeRecord(body: "plain message");
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));
		result.Candidates[0].MessageTemplate.Should().Be("plain message");
	}

	[Fact]
	public void DroppedAttributesCount_GoesTo_Properties_WhenNonzero()
	{
		var record = MakeRecord();
		record.DroppedAttributesCount = 3;
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));
		result.Candidates[0].Properties["otlp_dropped"].GetUInt32().Should().Be(3);
	}

	[Fact]
	public void DroppedAttributesCount_Zero_SkippedFromProperties()
	{
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(MakeRecord())));
		result.Candidates[0].Properties.Should().NotContainKey("otlp_dropped");
	}

	[Fact]
	public void Flags_GoesTo_Properties_WhenNonzero()
	{
		var record = MakeRecord();
		record.Flags = 0b1; // W3C trace flags "sampled" bit
		var result = OtlpLogsParser.Parse(SerializeRequest(Single(record)));
		result.Candidates[0].Properties["otlp_flags"].GetUInt32().Should().Be(1);
	}

	[Fact]
	public void Malformed_Protobuf_Reported_NotThrown()
	{
		// Random garbage bytes — definitely not a valid protobuf wire format.
		var result = OtlpLogsParser.Parse(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
		result.IsMalformed.Should().BeTrue();
		result.Candidates.Should().BeEmpty();
	}

	[Fact]
	public void Empty_Request_YieldsEmptyResult()
	{
		var result = OtlpLogsParser.Parse(SerializeRequest(new ExportLogsServiceRequest()));
		result.IsMalformed.Should().BeFalse();
		result.Candidates.Should().BeEmpty();
		result.Errors.Should().Be(0);
	}

	[Fact]
	public void MultipleRecords_AcrossScopes_AllProduced()
	{
		var req = new ExportLogsServiceRequest();
		var rl = new ResourceLogs { Resource = new Resource() };
		var scope1 = new ScopeLogs();
		scope1.LogRecords.Add(MakeRecord(body: "first"));
		scope1.LogRecords.Add(MakeRecord(body: "second"));
		var scope2 = new ScopeLogs();
		scope2.LogRecords.Add(MakeRecord(body: "third"));
		rl.ScopeLogs.Add(scope1);
		rl.ScopeLogs.Add(scope2);
		req.ResourceLogs.Add(rl);

		var result = OtlpLogsParser.Parse(SerializeRequest(req));

		result.Candidates.Select(c => c.Message).Should().BeEquivalentTo(["first", "second", "third"]);
	}

	[Fact]
	public void Partial_Batch_Malformed_Records_Counted_As_Errors_Not_Aborted()
	{
		// Two records: one missing timestamps (rejected), one valid — the valid one should
		// still land in Candidates, mirroring CLEF's partial-batch-ack semantics.
		var req = new ExportLogsServiceRequest();
		var rl = new ResourceLogs { Resource = new Resource() };
		var scope = new ScopeLogs();
		var bad = MakeRecord();
		bad.TimeUnixNano = 0;
		bad.ObservedTimeUnixNano = 0;
		scope.LogRecords.Add(bad);
		scope.LogRecords.Add(MakeRecord(body: "good"));
		rl.ScopeLogs.Add(scope);
		req.ResourceLogs.Add(rl);

		var result = OtlpLogsParser.Parse(SerializeRequest(req));

		result.Candidates.Should().HaveCount(1);
		result.Candidates[0].Message.Should().Be("good");
		result.Errors.Should().Be(1);
	}
}
