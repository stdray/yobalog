namespace YobaLog.Core.Ingestion;

public sealed record CleFParseError(CleFErrorKind Kind, string Message);
