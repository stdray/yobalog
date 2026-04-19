namespace YobaLog.Core.Kql;

public sealed class UnsupportedKqlException : Exception
{
	public UnsupportedKqlException(string message) : base(message) { }
	public UnsupportedKqlException(string message, Exception inner) : base(message, inner) { }
}
