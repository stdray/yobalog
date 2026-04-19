namespace YobaLog.Core.Storage.Sqlite;

public sealed record SqliteLogStoreOptions
{
	public string DataDirectory { get; init; } = "./data";
}
