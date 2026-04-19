namespace YobaLog.Core.Ingestion;

public sealed record CleFLineResult(int LineNumber, LogEventCandidate? Event, CleFParseError? Error)
{
	public bool IsSuccess => Event is not null;

	public static CleFLineResult Success(int lineNumber, LogEventCandidate candidate) =>
		new(lineNumber, candidate, null);

	public static CleFLineResult Failure(int lineNumber, CleFErrorKind kind, string message) =>
		new(lineNumber, null, new CleFParseError(kind, message));
}
