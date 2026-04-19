namespace YobaLog.Core.Ingestion;

public interface ICleFParser
{
	CleFLineResult ParseLine(string json, int lineNumber);

	IAsyncEnumerable<CleFLineResult> ParseAsync(Stream stream, CancellationToken ct);
}
