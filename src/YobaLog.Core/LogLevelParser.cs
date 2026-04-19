using System.Diagnostics.CodeAnalysis;

namespace YobaLog.Core;

public static class LogLevelParser
{
	public static LogLevel? Parse(string? s)
	{
		if (string.IsNullOrEmpty(s))
			return null;

		if (Eq(s, "Verbose") || Eq(s, "Trace")) return LogLevel.Verbose;
		if (Eq(s, "Debug")) return LogLevel.Debug;
		if (Eq(s, "Information") || Eq(s, "Info")) return LogLevel.Information;
		if (Eq(s, "Warning") || Eq(s, "Warn")) return LogLevel.Warning;
		if (Eq(s, "Error")) return LogLevel.Error;
		if (Eq(s, "Fatal") || Eq(s, "Critical")) return LogLevel.Fatal;
		return null;

		static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	public static bool TryParse([NotNullWhen(true)] string? s, out LogLevel level)
	{
		var r = Parse(s);
		level = r ?? default;
		return r.HasValue;
	}
}
