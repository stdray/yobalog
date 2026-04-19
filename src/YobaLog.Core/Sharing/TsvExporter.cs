using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YobaLog.Core.Sharing;

public static class TsvExporter
{
	static readonly string[] TopLevelColumns =
	[
		nameof(LogEvent.Id),
		nameof(LogEvent.Timestamp),
		nameof(LogEvent.Level),
		nameof(LogEvent.MessageTemplate),
		nameof(LogEvent.Message),
		nameof(LogEvent.Exception),
		nameof(LogEvent.TraceId),
		nameof(LogEvent.SpanId),
		nameof(LogEvent.EventId),
		"Properties",
	];

	public static async Task WriteAsync(
		IAsyncEnumerable<LogEvent> events,
		FieldMaskingPolicy policy,
		ValueMasker masker,
		TextWriter writer,
		CancellationToken ct)
	{
		var visibleColumns = TopLevelColumns
			.Where(c => policy.ModeFor(c) != MaskMode.Hide)
			.ToArray();

		await writer.WriteLineAsync(string.Join('\t', visibleColumns)).ConfigureAwait(false);

		await foreach (var e in events.WithCancellation(ct).ConfigureAwait(false))
		{
			var first = true;
			foreach (var col in visibleColumns)
			{
				if (!first) await writer.WriteAsync('\t').ConfigureAwait(false);
				first = false;
				var cell = RenderCell(col, e, policy, masker);
				await writer.WriteAsync(EscapeTsv(cell)).ConfigureAwait(false);
			}
			await writer.WriteAsync('\n').ConfigureAwait(false);
		}
	}

	static string RenderCell(string column, LogEvent e, FieldMaskingPolicy policy, ValueMasker masker)
	{
		var mode = policy.ModeFor(column);
		if (mode == MaskMode.Hide)
			return "";

		return column switch
		{
			nameof(LogEvent.Id) => e.Id.ToString(CultureInfo.InvariantCulture),
			nameof(LogEvent.Timestamp) => e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
			nameof(LogEvent.Level) => e.Level.ToString(),
			nameof(LogEvent.MessageTemplate) => MaybeMask(column, e.MessageTemplate, mode, masker),
			nameof(LogEvent.Message) => MaybeMask(column, e.Message, mode, masker),
			nameof(LogEvent.Exception) => MaybeMask(column, e.Exception ?? "", mode, masker),
			nameof(LogEvent.TraceId) => MaybeMask(column, e.TraceId ?? "", mode, masker),
			nameof(LogEvent.SpanId) => MaybeMask(column, e.SpanId ?? "", mode, masker),
			nameof(LogEvent.EventId) => e.EventId?.ToString(CultureInfo.InvariantCulture) ?? "",
			"Properties" => RenderProperties(e.Properties, policy, masker),
			_ => "",
		};
	}

	static string MaybeMask(string path, string value, MaskMode mode, ValueMasker masker) =>
		mode switch
		{
			MaskMode.Mask => masker.Mask(path, value),
			MaskMode.Keep => value,
			_ => "",
		};

	static string RenderProperties(
		ImmutableDictionary<string, JsonElement> props,
		FieldMaskingPolicy policy,
		ValueMasker masker)
	{
		if (props.IsEmpty)
			return "";

		using var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream))
		{
			w.WriteStartObject();
			foreach (var (k, v) in props)
			{
				var path = "Properties." + k;
				var mode = policy.ModeFor(path);
				if (mode == MaskMode.Hide)
					continue;
				w.WritePropertyName(k);
				if (mode == MaskMode.Mask)
					w.WriteStringValue(masker.Mask(path, JsonValueToString(v)));
				else
					v.WriteTo(w);
			}
			w.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	static string JsonValueToString(JsonElement e) => e.ValueKind switch
	{
		JsonValueKind.String => e.GetString() ?? "",
		JsonValueKind.Null => "",
		JsonValueKind.Undefined => "",
		_ => e.GetRawText(),
	};

	static string EscapeTsv(string s)
	{
		if (string.IsNullOrEmpty(s))
			return "";
		if (s.IndexOfAny(['\t', '\n', '\r']) < 0)
			return s;
		var sb = new StringBuilder(s.Length);
		foreach (var c in s)
		{
			switch (c)
			{
				case '\t': sb.Append(' '); break;
				case '\n': sb.Append(' '); break;
				case '\r': break;
				default: sb.Append(c); break;
			}
		}
		return sb.ToString();
	}
}
