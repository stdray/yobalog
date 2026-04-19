namespace YobaLog.Core.SavedQueries;

public sealed record SavedQuery(
	long Id,
	string Name,
	string Kql,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
