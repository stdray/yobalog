using System.Collections.Immutable;

namespace YobaLog.Core.Sharing;

public sealed record ShareToken(
	WorkspaceId Workspace,
	string Kql,
	DateTimeOffset ExpiresAt,
	ImmutableArray<byte> Salt,
	ImmutableDictionary<string, MaskMode> Modes);
