using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using YobaLog.Core.Tracing;
using YobaLog.Web.Ingestion;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ProtoSpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;
using DomainSpanKind = YobaLog.Core.Tracing.SpanKind;

namespace YobaLog.Tests.Web;

// Unit tests for OtlpTracesParser — decision-log 2026-04-21 Phase H.2 mapping: trace_id /
// span_id / parent_span_id hex encoding, all-zero parent = null, kind-enum offset fix
// (OTLP is 1-indexed with Unspecified, domain is 0-indexed matching ActivityKind),
// resource.attributes > scope.attributes > span.attributes on collision.
public sealed class OtlpTracesParserTests
{
	static readonly byte[] TraceId16 = [
		0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
		0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];
	static readonly byte[] SpanId8 = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17];
	static readonly byte[] ParentId8 = [0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27];
	const string ExpectedTraceHex = "000102030405060708090a0b0c0d0e0f";
	const string ExpectedSpanHex = "1011121314151617";
	const string ExpectedParentHex = "2021222324252627";

	static byte[] SerializeRequest(ExportTraceServiceRequest request) => request.ToByteArray();

	static ExportTraceServiceRequest Single(ProtoSpan span, params KeyValue[] resourceAttrs)
	{
		var resource = new Resource();
		foreach (var kv in resourceAttrs) resource.Attributes.Add(kv);
		var scope = new ScopeSpans();
		scope.Spans.Add(span);
		var rs = new ResourceSpans { Resource = resource };
		rs.ScopeSpans.Add(scope);
		var req = new ExportTraceServiceRequest();
		req.ResourceSpans.Add(rs);
		return req;
	}

	static ProtoSpan MakeSpan(
		ulong startNs = 1_700_000_000_000_000_000UL,
		ulong endNs = 1_700_000_000_100_000_000UL,
		string name = "op",
		ProtoSpanKind kind = ProtoSpanKind.Internal,
		bool withParent = false)
	{
		var s = new ProtoSpan
		{
			TraceId = ByteString.CopyFrom(TraceId16),
			SpanId = ByteString.CopyFrom(SpanId8),
			Name = name,
			Kind = kind,
			StartTimeUnixNano = startNs,
			EndTimeUnixNano = endNs,
		};
		if (withParent) s.ParentSpanId = ByteString.CopyFrom(ParentId8);
		return s;
	}

	[Fact]
	public void TraceId_And_SpanId_HexEncoded_Lowercase()
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan())));
		result.Spans.Should().HaveCount(1);
		result.Spans[0].TraceId.Should().Be(ExpectedTraceHex);
		result.Spans[0].SpanId.Should().Be(ExpectedSpanHex);
	}

	[Fact]
	public void ParentSpanId_Absent_MappedToNull()
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan(withParent: false))));
		result.Spans[0].ParentSpanId.Should().BeNull();
	}

	[Fact]
	public void ParentSpanId_Present_HexEncoded()
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan(withParent: true))));
		result.Spans[0].ParentSpanId.Should().Be(ExpectedParentHex);
	}

	[Fact]
	public void AllZero_ParentSpanId_TreatedAsAbsent()
	{
		var span = MakeSpan();
		span.ParentSpanId = ByteString.CopyFrom(new byte[8]);
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));
		result.Spans[0].ParentSpanId.Should().BeNull();
	}

	[Fact]
	public void AllZero_TraceId_Rejects_Span()
	{
		var span = MakeSpan();
		span.TraceId = ByteString.CopyFrom(new byte[16]);
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));
		result.Spans.Should().BeEmpty();
		result.Errors.Should().Be(1);
	}

	[Fact]
	public void ZeroStartTime_Rejects_Span()
	{
		var span = MakeSpan(startNs: 0UL, endNs: 0UL);
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));
		result.Spans.Should().BeEmpty();
		result.Errors.Should().Be(1);
	}

	[Fact]
	public void StartTime_And_Duration_Derived_From_UnixNanos()
	{
		// 1_700_000_000 seconds = 2023-11-14 22:13:20 UTC; 100ms duration.
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan(
			startNs: 1_700_000_000_000_000_000UL,
			endNs: 1_700_000_000_100_000_000UL))));
		result.Spans[0].StartTime.ToUnixTimeMilliseconds().Should().Be(1_700_000_000_000);
		result.Spans[0].Duration.Should().Be(TimeSpan.FromMilliseconds(100));
	}

	[Fact]
	public void EndTime_Zero_Yields_Zero_Duration()
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan(endNs: 0UL))));
		result.Spans.Should().HaveCount(1);
		result.Spans[0].Duration.Should().Be(TimeSpan.Zero);
	}

	[Theory]
	[InlineData(ProtoSpanKind.Internal, DomainSpanKind.Internal)]
	[InlineData(ProtoSpanKind.Server, DomainSpanKind.Server)]
	[InlineData(ProtoSpanKind.Client, DomainSpanKind.Client)]
	[InlineData(ProtoSpanKind.Producer, DomainSpanKind.Producer)]
	[InlineData(ProtoSpanKind.Consumer, DomainSpanKind.Consumer)]
	[InlineData(ProtoSpanKind.Unspecified, DomainSpanKind.Internal)]
	public void Kind_Proto_MapsTo_DomainSpanKind(ProtoSpanKind proto, DomainSpanKind expected)
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan(kind: proto))));
		result.Spans[0].Kind.Should().Be(expected);
	}

	[Fact]
	public void Status_Code_And_Message_Preserved()
	{
		var span = MakeSpan();
		span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = "db timeout" };
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));
		result.Spans[0].Status.Should().Be(SpanStatusCode.Error);
		result.Spans[0].StatusDescription.Should().Be("db timeout");
	}

	[Fact]
	public void Status_Unset_Maps_To_Unset()
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(MakeSpan())));
		result.Spans[0].Status.Should().Be(SpanStatusCode.Unset);
		result.Spans[0].StatusDescription.Should().BeNull();
	}

	[Fact]
	public void Attributes_Flattened_Into_SpanAttributes()
	{
		var span = MakeSpan();
		span.Attributes.Add(new KeyValue { Key = "user.id", Value = new AnyValue { StringValue = "42" } });
		span.Attributes.Add(new KeyValue { Key = "count", Value = new AnyValue { IntValue = 7 } });

		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));

		result.Spans[0].Attributes["user.id"].GetString().Should().Be("42");
		result.Spans[0].Attributes["count"].GetInt32().Should().Be(7);
	}

	[Fact]
	public void ResourceAttributes_Win_OnCollision_With_SpanAttributes()
	{
		// Resource says service.name=yobalog; span tries to override.
		var span = MakeSpan();
		span.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "not-yobalog" } });

		var resourceAttr = new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "yobalog" } };
		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span, resourceAttr)));

		result.Spans[0].Attributes["service.name"].GetString().Should().Be("yobalog");
	}

	[Fact]
	public void Events_Preserved_With_Timestamp_And_Attributes()
	{
		var span = MakeSpan();
		var evt = new ProtoSpan.Types.Event
		{
			TimeUnixNano = 1_700_000_000_050_000_000UL,
			Name = "exception",
		};
		evt.Attributes.Add(new KeyValue { Key = "exception.type", Value = new AnyValue { StringValue = "IOException" } });
		span.Events.Add(evt);

		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));

		result.Spans[0].Events.Should().HaveCount(1);
		result.Spans[0].Events[0].Name.Should().Be("exception");
		result.Spans[0].Events[0].Attributes["exception.type"].GetString().Should().Be("IOException");
	}

	[Fact]
	public void Links_Preserved_With_Hex_Ids()
	{
		var span = MakeSpan();
		span.Links.Add(new ProtoSpan.Types.Link
		{
			TraceId = ByteString.CopyFrom(TraceId16),
			SpanId = ByteString.CopyFrom(SpanId8),
		});

		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));

		result.Spans[0].Links.Should().HaveCount(1);
		result.Spans[0].Links[0].TraceId.Should().Be(ExpectedTraceHex);
		result.Spans[0].Links[0].SpanId.Should().Be(ExpectedSpanHex);
	}

	[Fact]
	public void DroppedCounts_Flattened_Only_When_Nonzero()
	{
		var span = MakeSpan();
		span.DroppedAttributesCount = 3;
		span.DroppedEventsCount = 0;
		span.DroppedLinksCount = 5;

		var result = OtlpTracesParser.Parse(SerializeRequest(Single(span)));
		var attrs = result.Spans[0].Attributes;

		attrs["otlp_dropped_attributes"].GetUInt32().Should().Be(3);
		attrs.Should().NotContainKey("otlp_dropped_events");
		attrs["otlp_dropped_links"].GetUInt32().Should().Be(5);
	}

	[Fact]
	public void Malformed_Protobuf_Reported_NotThrown()
	{
		var result = OtlpTracesParser.Parse(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
		result.IsMalformed.Should().BeTrue();
		result.Spans.Should().BeEmpty();
	}

	[Fact]
	public void Empty_Request_YieldsEmptyResult()
	{
		var result = OtlpTracesParser.Parse(SerializeRequest(new ExportTraceServiceRequest()));
		result.IsMalformed.Should().BeFalse();
		result.Spans.Should().BeEmpty();
		result.Errors.Should().Be(0);
	}

	[Fact]
	public void MultipleSpans_AcrossScopes_AllProduced()
	{
		var req = new ExportTraceServiceRequest();
		var rs = new ResourceSpans { Resource = new Resource() };
		var scope1 = new ScopeSpans();
		var span1 = MakeSpan(name: "first");
		span1.SpanId = ByteString.CopyFrom([0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8]);
		scope1.Spans.Add(span1);
		var span2 = MakeSpan(name: "second");
		span2.SpanId = ByteString.CopyFrom([0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8]);
		scope1.Spans.Add(span2);
		var scope2 = new ScopeSpans();
		var span3 = MakeSpan(name: "third");
		span3.SpanId = ByteString.CopyFrom([0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8]);
		scope2.Spans.Add(span3);
		rs.ScopeSpans.Add(scope1);
		rs.ScopeSpans.Add(scope2);
		req.ResourceSpans.Add(rs);

		var result = OtlpTracesParser.Parse(SerializeRequest(req));

		result.Spans.Select(s => s.Name).Should().BeEquivalentTo(["first", "second", "third"]);
	}

	[Fact]
	public void Partial_Batch_Malformed_Spans_Counted_As_Errors_Not_Aborted()
	{
		// One span with missing trace_id (rejected), one valid.
		var req = new ExportTraceServiceRequest();
		var rs = new ResourceSpans { Resource = new Resource() };
		var scope = new ScopeSpans();
		var bad = MakeSpan();
		bad.TraceId = ByteString.CopyFrom(new byte[16]);
		scope.Spans.Add(bad);
		var good = MakeSpan(name: "good");
		good.SpanId = ByteString.CopyFrom([0xff, 0xfe, 0xfd, 0xfc, 0xfb, 0xfa, 0xf9, 0xf8]);
		scope.Spans.Add(good);
		rs.ScopeSpans.Add(scope);
		req.ResourceSpans.Add(rs);

		var result = OtlpTracesParser.Parse(SerializeRequest(req));

		result.Spans.Should().HaveCount(1);
		result.Spans[0].Name.Should().Be("good");
		result.Errors.Should().Be(1);
	}
}
