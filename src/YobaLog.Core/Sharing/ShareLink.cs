using System.Collections.Immutable;

namespace YobaLog.Core.Sharing;

public sealed record ShareLink(
	string Id,
	string Kql,
	DateTimeOffset CreatedAt,
	DateTimeOffset ExpiresAt,
	ImmutableArray<byte> Salt,
	ImmutableArray<string> Columns,
	ImmutableDictionary<string, MaskMode> Modes);
