namespace YobaLog.Core.Ingestion;

public enum CleFErrorKind
{
	MalformedJson,
	MissingTimestamp,
	InvalidTimestamp,
	InvalidLevel,
}
