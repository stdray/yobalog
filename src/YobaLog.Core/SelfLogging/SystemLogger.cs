using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace YobaLog.Core.SelfLogging;

sealed class SystemLogger : ILogger
{
	readonly string _category;
	readonly SystemLoggerOptions _options;
	readonly ChannelWriter<LogEventCandidate> _writer;
	readonly TimeProvider _time;
	readonly JsonElement _categoryElement;

	public SystemLogger(string category, SystemLoggerOptions options, ChannelWriter<LogEventCandidate> writer, TimeProvider time)
	{
		_category = category;
		_options = options;
		_writer = writer;
		_time = time;
		_categoryElement = JsonSerializer.SerializeToElement(category);
	}

	public bool IsEnabled(MelLogLevel logLevel) =>
		logLevel != MelLogLevel.None &&
		_category.StartsWith(_options.CategoryPrefix, StringComparison.Ordinal) &&
		logLevel >= ToMelLevel(_options.MinimumLevel);

	public void Log<TState>(
		MelLogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
			return;

		ArgumentNullException.ThrowIfNull(formatter);

		var message = formatter(state, exception);
		var template = ExtractTemplate(state) ?? message;

		var props = _options.StaticProperties.SetItem("SourceContext", _categoryElement);

		// Activity.Current is ambient — AspNetCoreInstrumentation sets the root span,
		// domain code extends it via ActivitySource.StartActivity. Reading directly
		// here bypasses MEL scope plumbing (SystemLogger returns null from BeginScope)
		// while producing the same TraceId/SpanId stamping as ActivityTrackingOptions would.
		var activity = Activity.Current;
		var traceId = activity?.TraceId.ToHexString();
		var spanId = activity?.SpanId.ToHexString();

		var candidate = new LogEventCandidate(
			_time.GetUtcNow(),
			FromMelLevel(logLevel),
			template,
			message,
			exception?.ToString(),
			string.IsNullOrEmpty(traceId) || traceId == "00000000000000000000000000000000" ? null : traceId,
			string.IsNullOrEmpty(spanId) || spanId == "0000000000000000" ? null : spanId,
			eventId.Id == 0 ? null : eventId.Id,
			props);

		_writer.TryWrite(candidate);
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	static string? ExtractTemplate<TState>(TState state)
	{
		if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
		{
			foreach (var kv in kvs)
			{
				if (kv.Key == "{OriginalFormat}" && kv.Value is string s)
					return s;
			}
		}
		return null;
	}

	static MelLogLevel ToMelLevel(LogLevel level) => level switch
	{
		LogLevel.Verbose => MelLogLevel.Trace,
		LogLevel.Debug => MelLogLevel.Debug,
		LogLevel.Information => MelLogLevel.Information,
		LogLevel.Warning => MelLogLevel.Warning,
		LogLevel.Error => MelLogLevel.Error,
		LogLevel.Fatal => MelLogLevel.Critical,
		_ => MelLogLevel.Information,
	};

	static LogLevel FromMelLevel(MelLogLevel level) => level switch
	{
		MelLogLevel.Trace => LogLevel.Verbose,
		MelLogLevel.Debug => LogLevel.Debug,
		MelLogLevel.Information => LogLevel.Information,
		MelLogLevel.Warning => LogLevel.Warning,
		MelLogLevel.Error => LogLevel.Error,
		MelLogLevel.Critical => LogLevel.Fatal,
		_ => LogLevel.Information,
	};
}
