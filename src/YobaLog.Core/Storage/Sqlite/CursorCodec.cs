using System.Buffers.Binary;

namespace YobaLog.Core.Storage.Sqlite;

static class CursorCodec
{
	public const int Size = 16;

	public static ReadOnlyMemory<byte> Encode(long timestampMs, long id)
	{
		var bytes = new byte[Size];
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(0, 8), timestampMs);
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8, 8), id);
		return bytes;
	}

	public static (long TimestampMs, long Id) Decode(ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length != Size)
			throw new ArgumentException($"cursor must be {Size} bytes, got {bytes.Length}", nameof(bytes));
		var ts = BinaryPrimitives.ReadInt64BigEndian(bytes[..8]);
		var id = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
		return (ts, id);
	}
}
