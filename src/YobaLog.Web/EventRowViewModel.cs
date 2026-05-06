using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using YobaLog.Core;
using LogLevel = YobaLog.Core.LogLevel;

namespace YobaLog.Web;

public sealed record EventRowViewModel(
    long? Id,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string MessageTemplate,
    string Message,
    string? Exception,
    string? TraceId,
    string? SpanId,
    int? EventId,
    ImmutableDictionary<string, JsonElement> Properties,
    bool IsLive)
{
    public static EventRowViewModel FromStored(LogEvent e) => new(
        e.Id,
        e.Timestamp,
        e.Level,
        e.MessageTemplate,
        e.Message,
        e.Exception,
        e.TraceId,
        e.SpanId,
        e.EventId,
        e.Properties,
        IsLive: false);

    public static EventRowViewModel FromLive(LogEventCandidate c) => new(
        Id: null,
        c.Timestamp,
        c.Level,
        c.MessageTemplate,
        c.Message,
        c.Exception,
        c.TraceId,
        c.SpanId,
        c.EventId,
        c.Properties,
        IsLive: true);

    // RenderedMessage substitutes {PropertyName} placeholders with actual values from
    // Properties when the sender left Message == MessageTemplate (no pre-rendered @m).
    // Substituted values are wrapped in <mark class="msg-sub …"> for visual distinction.
    // When the sender already rendered Message, it is HTML-encoded and used as-is.
    public HtmlString RenderedMessage => Message == MessageTemplate
        ? RenderMessageTemplate(MessageTemplate, Properties)
        : new HtmlString(HtmlEncoder.Default.Encode(Message));

    static readonly Regex _placeholderRegex = new(@"\{([@$]?)([^{}]+)\}", RegexOptions.Compiled);

    static HtmlString RenderMessageTemplate(string template, ImmutableDictionary<string, JsonElement> properties)
    {
        if (string.IsNullOrEmpty(template))
            return HtmlString.Empty;

        var sb = new StringBuilder(template.Length * 2);
        var pos = 0;

        foreach (Match m in _placeholderRegex.Matches(template))
        {
            if (m.Index > pos)
                AppendLiteral(sb, template[pos..m.Index]);

            var key = m.Groups[2].Value;

            if (properties.TryGetValue(key, out var value))
            {
                var display = PropertyForDisplay(value).Display;
                sb.Append("<mark class=\"msg-sub bg-primary/20 rounded px-0.5 font-mono text-xs\">");
                sb.Append(HtmlEncoder.Default.Encode(display));
                sb.Append("</mark>");
            }
            else
            {
                sb.Append(HtmlEncoder.Default.Encode(m.Value));
            }

            pos = m.Index + m.Length;
        }

        if (pos < template.Length)
            AppendLiteral(sb, template[pos..]);

        return new HtmlString(sb.ToString());
    }

    static void AppendLiteral(StringBuilder sb, string literal)
    {
        var encoded = HtmlEncoder.Default.Encode(literal);
        // Serilog: {{ → {, }} → }
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '{' && i + 1 < encoded.Length && encoded[i + 1] == '{')
            {
                sb.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < encoded.Length && encoded[i + 1] == '}')
            {
                sb.Append('}');
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
    }

    public static string LevelBadge(LogLevel l) => l switch
    {
        LogLevel.Verbose => "badge-ghost",
        LogLevel.Debug => "badge-ghost",
        LogLevel.Information => "badge-info",
        LogLevel.Warning => "badge-warning",
        LogLevel.Error => "badge-error",
        LogLevel.Fatal => "badge-error",
        _ => "badge-ghost",
    };

    public static string KqlString(string? s) =>
        "'" + (s ?? "")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal) + "'";

    public static string KqlDatetime(DateTimeOffset dt) =>
        "datetime(" + IsoUtc(dt) + ")";

    public static string IsoUtc(DateTimeOffset dt) =>
        dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // Human-readable shape for clipboard copy. Not CLEF (`@t`/`@l`/…) — the canonical
    // short keys are great on the wire and unreadable pasted into a ticket / chat.
    // Properties sit nested under `properties` to avoid collisions with top-level
    // names (User, Count, Level, …). Relaxed escaping keeps Cyrillic / emoji /
    // `<`,`>` etc. intact in the clipboard; Razor still HTML-encodes the attribute
    // value on render, so the data-copy round-trip is safe.
    static readonly JsonWriterOptions CopyJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
    };

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, CopyJsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("timestamp", IsoUtc(Timestamp));
            w.WriteString("level", Level.ToString());
            w.WriteString("messageTemplate", MessageTemplate);
            if (!string.Equals(Message, MessageTemplate, StringComparison.Ordinal))
                w.WriteString("message", Message);
            if (Exception is not null)
                w.WriteString("exception", Exception);
            if (EventId is not null)
                w.WriteNumber("eventId", EventId.Value);
            if (TraceId is not null)
                w.WriteString("traceId", TraceId);
            if (SpanId is not null)
                w.WriteString("spanId", SpanId);
            if (Properties.Count > 0)
            {
                w.WritePropertyName("properties");
                w.WriteStartObject();
                foreach (var (key, value) in Properties)
                {
                    w.WritePropertyName(key);
                    value.WriteTo(w);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Returns display text + KQL literal for a property value.
    /// KQL literal is null if the value isn't filterable as a string (e.g. nested object/array).</summary>
    public static (string Display, string? KqlLiteral) PropertyForDisplay(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => (v.GetString() ?? "", KqlString(v.GetString())),
        JsonValueKind.Null or JsonValueKind.Undefined => ("(null)", null),
        JsonValueKind.Number => (v.GetRawText(), v.GetRawText()),
        JsonValueKind.True => ("true", "true"),
        JsonValueKind.False => ("false", "false"),
        _ => (v.GetRawText(), null),
    };
}
