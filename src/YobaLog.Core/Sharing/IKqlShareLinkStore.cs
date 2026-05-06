namespace YobaLog.Core.Sharing;

public interface IKqlShareLinkStore
{
    ValueTask InitializeAsync(CancellationToken ct);
    ValueTask<KqlShareLink> CreateAsync(WorkspaceId workspace, string kql, DateTimeOffset expiresAt, CancellationToken ct);
    ValueTask<KqlShareLink?> GetAsync(string id, CancellationToken ct);
    ValueTask<bool> DeleteAsync(string id, CancellationToken ct);
    ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct);
}
