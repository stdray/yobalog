using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace YobaLog.Core.Ingestion;

public sealed class CleFParser : ICleFParser
{
	public CleFLineResult ParseLine(string json, int lineNumber)
	{
		if (string.IsNullOrWhiteSpace(json))
			return CleFLineResult.Failure(lineNumber, CleFErrorKind.MalformedJson, "line is empty");

		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(json);
		}
		catch (JsonException ex)
		{
			return CleFLineResult.Failure(lineNumber, CleFErrorKind.MalformedJson, ex.Message);
		}

		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return CleFLineResult.Failure(lineNumber, CleFErrorKind.MalformedJson, "expected JSON object at root");

			if (!root.TryGetProperty("@t", out var tsElem))
				return CleFLineResult.Failure(lineNumber, CleFErrorKind.MissingTimestamp, "@t is required");

			if (tsElem.ValueKind != JsonValueKind.String ||
				!DateTimeOffset.TryParse(
					tsElem.GetString(),
					CultureInfo.InvariantCulture,
					DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
					out var timestamp))
			{
				return CleFLineResult.Failure(lineNumber, CleFErrorKind.InvalidTimestamp, "@t is not a valid ISO 8601 timestamp");
			}

			var level = LogLevel.Information;
			if (root.TryGetProperty("@l", out var lvlElem))
			{
				if (lvlElem.ValueKind != JsonValueKind.String)
					return CleFLineResult.Failure(lineNumber, CleFErrorKind.InvalidLevel, "@l must be a string");
				var s = lvlElem.GetString();
				var parsed = LogLevelParser.Parse(s);
				if (parsed is null)
					return CleFLineResult.Failure(lineNumber, CleFErrorKind.InvalidLevel, $"unknown level: '{s}'");
				level = parsed.Value;
			}

			var message = GetStringOrNull(root, "@m");
			var template = GetStringOrNull(root, "@mt");
			var exception = GetStringOrNull(root, "@x");
			var traceId = GetStringOrNull(root, "@tr");
			var spanId = GetStringOrNull(root, "@sp");
			var eventId = GetInt32OrNull(root, "@i");

			var properties = ExtractProperties(root);

			var candidate = new LogEventCandidate(
				timestamp,
				level,
				template ?? message ?? string.Empty,
				message ?? template ?? string.Empty,
				exception,
				traceId,
				spanId,
				eventId,
				properties);

			return CleFLineResult.Success(lineNumber, candidate);
		}
	}

	public async IAsyncEnumerable<CleFLineResult> ParseAsync(
		Stream stream,
		[EnumeratorCancellation] CancellationToken ct)
	{
		using var reader = new StreamReader(
			stream,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: false,
			leaveOpen: true);

		var lineNumber = 0;
		while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
		{
			lineNumber++;
			if (string.IsNullOrWhiteSpace(line))
				continue;
			yield return ParseLine(line, lineNumber);
		}
	}

	static string? GetStringOrNull(JsonElement root, string name) =>
		root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
			? el.GetString()
			: null;

	static int? GetInt32OrNull(JsonElement root, string name) =>
		root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)
			? i
			: null;

	static ImmutableDictionary<string, JsonElement> ExtractProperties(JsonElement root)
	{
		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var prop in root.EnumerateObject())
		{
			var name = prop.Name;
			if (name.Length == 0)
				continue;
			if (name[0] == '@')
			{
				if (name.Length >= 2 && name[1] == '@')
					builder[name[1..]] = prop.Value.Clone();
			}
			else
			{
				builder[name] = prop.Value.Clone();
			}
		}
		return builder.ToImmutable();
	}
}
