using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using YobaLog.Core.SelfLogging;
using LogLevel = YobaLog.Core.LogLevel;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace YobaLog.Tests.SelfLogging;

public sealed class SystemLoggerProviderTests
{
	static SystemLoggerProvider Create(SystemLoggerOptions? options = null, TimeProvider? time = null) =>
		new(Options.Create(options ?? new SystemLoggerOptions()), time);

	[Fact]
	public void YobaLogCategory_EventQueued()
	{
		var time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero));
		var provider = Create(time: time);
		var logger = provider.CreateLogger("YobaLog.Test");

		logger.LogInformation("hello {Name}", "world");

		provider.Reader.TryRead(out var ev).Should().BeTrue();
		ev!.Message.Should().Be("hello world");
		ev.MessageTemplate.Should().Be("hello {Name}");
		ev.Level.Should().Be(LogLevel.Information);
		ev.Timestamp.Should().Be(time.GetUtcNow());
		ev.Properties["SourceContext"].GetString().Should().Be("YobaLog.Test");
	}

	[Fact]
	public void NonYobaLogCategory_NotQueued()
	{
		var provider = Create();
		var logger = provider.CreateLogger("Microsoft.AspNetCore.Request");

		logger.LogInformation("not mine");

		provider.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void BelowMinimumLevel_NotQueued()
	{
		var provider = Create(new SystemLoggerOptions { MinimumLevel = LogLevel.Warning });
		var logger = provider.CreateLogger("YobaLog.Test");

		logger.LogInformation("skipped");
		logger.LogWarning("kept");

		provider.Reader.TryRead(out var ev).Should().BeTrue();
		ev!.Message.Should().Be("kept");
		provider.Reader.TryRead(out _).Should().BeFalse();
	}

	[Fact]
	public void IsEnabled_Matches()
	{
		var provider = Create();
		var yoba = provider.CreateLogger("YobaLog.Ingestion");
		var msft = provider.CreateLogger("Microsoft.AspNetCore");

		yoba.IsEnabled(MelLogLevel.Information).Should().BeTrue();
		msft.IsEnabled(MelLogLevel.Information).Should().BeFalse();
		yoba.IsEnabled(MelLogLevel.None).Should().BeFalse();
	}

	[Fact]
	public void QueueFull_Drops_DoesNotBlock()
	{
		var provider = Create(new SystemLoggerOptions { QueueCapacity = 2 });
		var logger = provider.CreateLogger("YobaLog.Test");

		logger.LogInformation("1");
		logger.LogInformation("2");
		logger.LogInformation("3"); // dropped

		var count = 0;
		while (provider.Reader.TryRead(out _))
			count++;
		count.Should().Be(2);
	}

	[Fact]
	public void Exception_Captured()
	{
		var provider = Create();
		var logger = provider.CreateLogger("YobaLog.Test");

		logger.LogError(new InvalidOperationException("boom"), "failure {Code}", 42);

		provider.Reader.TryRead(out var ev).Should().BeTrue();
		ev!.Level.Should().Be(LogLevel.Error);
		ev.Exception.Should().Contain("InvalidOperationException").And.Contain("boom");
		ev.MessageTemplate.Should().Be("failure {Code}");
	}

	[Fact]
	public void SameCategory_ReturnsSameLogger()
	{
		var provider = Create();
		var a = provider.CreateLogger("YobaLog.Test");
		var b = provider.CreateLogger("YobaLog.Test");
		a.Should().BeSameAs(b);
	}
}
