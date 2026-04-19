using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YobaLog.Core.Sharing;

public static class TsvExporter
{
	public static async Task WriteAsync(
		IAsyncEnumerable<LogEvent> events,
		IReadOnlyList<string> columns,
		FieldMaskingPolicy policy,
		ValueMasker masker,
		TextWriter writer,
		CancellationToken ct)
	{
		var visible = columns
			.Where(c => policy.ModeFor(c) != MaskMode.Hide)
			.ToArray();

		await writer.WriteLineAsync(string.Join('\t', visible)).ConfigureAwait(false);

		await foreach (var e in events.WithCancellation(ct).ConfigureAwait(false))
		{
			var first = true;
			foreach (var col in visible)
			{
				if (!first) await writer.WriteAsync('\t').ConfigureAwait(false);
				first = false;
				var mode = policy.ModeFor(col);
				var cell = RenderCell(col, e, mode, masker);
				await writer.WriteAsync(EscapeTsv(cell)).ConfigureAwait(false);
			}
			await writer.WriteAsync('\n').ConfigureAwait(false);
		}
	}

	static string RenderCell(string column, LogEvent e, MaskMode mode, ValueMasker masker)
	{
		var raw = LookupScalar(column, e) ?? LookupProperty(e.Properties, column);
		return mode == MaskMode.Mask ? masker.Mask(column, raw) : raw;
	}

	// Flat column namespace: top-level scalars share the lookup path with Properties.*.
	// On name collision, top-level wins — property keys shadowed by a scalar name are invisible in TSV.
	static string? LookupScalar(string column, LogEvent e) => column switch
	{
		nameof(LogEvent.Id) => e.Id.ToString(CultureInfo.InvariantCulture),
		nameof(LogEvent.Timestamp) => e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
		nameof(LogEvent.Level) => e.Level.ToString(),
		nameof(LogEvent.MessageTemplate) => e.MessageTemplate,
		nameof(LogEvent.Message) => e.Message,
		nameof(LogEvent.Exception) => e.Exception ?? "",
		nameof(LogEvent.TraceId) => e.TraceId ?? "",
		nameof(LogEvent.SpanId) => e.SpanId ?? "",
		nameof(LogEvent.EventId) => e.EventId?.ToString(CultureInfo.InvariantCulture) ?? "",
		_ => null,
	};

	static string LookupProperty(ImmutableDictionary<string, JsonElement> props, string key) =>
		props.TryGetValue(key, out var v) ? JsonValueToString(v) : "";

	static string JsonValueToString(JsonElement e) => e.ValueKind switch
	{
		JsonValueKind.String => e.GetString() ?? "",
		JsonValueKind.Null or JsonValueKind.Undefined => "",
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
