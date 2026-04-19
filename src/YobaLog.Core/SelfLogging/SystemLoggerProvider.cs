using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YobaLog.Core.SelfLogging;

public sealed class SystemLoggerProvider : ILoggerProvider
{
	readonly SystemLoggerOptions _options;
	readonly TimeProvider _time;
	readonly Channel<LogEventCandidate> _channel;
	readonly ConcurrentDictionary<string, SystemLogger> _loggers = new(StringComparer.Ordinal);

	public SystemLoggerProvider(IOptions<SystemLoggerOptions> options, TimeProvider? time = null)
	{
		_options = options.Value;
		_time = time ?? TimeProvider.System;
		_channel = Channel.CreateBounded<LogEventCandidate>(new BoundedChannelOptions(_options.QueueCapacity)
		{
			FullMode = BoundedChannelFullMode.DropWrite,
			SingleReader = true,
			SingleWriter = false,
		});
	}

	internal ChannelReader<LogEventCandidate> Reader => _channel.Reader;

	internal SystemLoggerOptions Options => _options;

	public ILogger CreateLogger(string categoryName) =>
		_loggers.GetOrAdd(categoryName, name => new SystemLogger(name, _options, _channel.Writer, _time));

	public void Dispose() => _channel.Writer.TryComplete();
}
