using System.Collections.Immutable;
using System.Text.Json;

namespace YobaLog.Web;

public sealed record QueryRequest(string Workspace, string Kql, string? Cursor);

public sealed record QueryResponse(
    ImmutableArray<string> Columns,
    ImmutableArray<ImmutableArray<JsonElement?>> Rows,
    string? Cursor,
    bool Truncated);
