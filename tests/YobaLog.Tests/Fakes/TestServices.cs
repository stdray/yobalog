using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YobaLog.Core.Admin;
using YobaLog.Core.Admin.Sqlite;
using YobaLog.Core.Auth;
using YobaLog.Core.Auth.Sqlite;
using YobaLog.Core.Retention;
using YobaLog.Core.Retention.Sqlite;
using YobaLog.Core.SavedQueries;
using YobaLog.Core.SavedQueries.Sqlite;
using YobaLog.Core.Sharing;
using YobaLog.Core.Sharing.Sqlite;
using YobaLog.Core.Storage;
using YobaLog.Core.Storage.Sqlite;
using YobaLog.Core.Tracing;
using YobaLog.Core.Tracing.Sqlite;

namespace YobaLog.Tests.Fakes;

// Single registration point for the SQLite-backed stores in unit tests. Tests resolve
// concrete store types (or their interfaces) from the returned ServiceProvider — no
// `new SqliteFooStore(new SqliteConnectionFactory(Options.Create(...)))` ceremony in
// fixture ctors.
//
// Each call returns a fresh isolated container with `DataDirectory = dataDirectory`.
// The ServiceProvider is IDisposable; tests own its lifetime (typically dispose in
// IAsyncLifetime.DisposeAsync alongside the temp directory).
static class TestServices
{
    public static ServiceProvider BuildSqliteStores(string dataDirectory)
    {
        var services = new ServiceCollection();
        // SqliteLogStoreOptions is init-only — register via the IOptions<T> shape directly
        // rather than `Configure(o => o.DataDirectory = ...)` (which can't reach init setters).
        services.AddSingleton<IOptions<SqliteLogStoreOptions>>(
            Options.Create(new SqliteLogStoreOptions { DataDirectory = dataDirectory }));
        services.AddSingleton<SqliteConnectionFactory>();

        // Concrete registrations — tests can resolve the concrete class when they need
        // backend-specific assertions, or the interface when the test is provider-agnostic.
        services.AddSingleton<SqliteLogStore>();
        services.AddSingleton<SqliteSpanStore>();
        services.AddSingleton<SqliteSavedQueryStore>();
        services.AddSingleton<SqliteShareLinkStore>();
        services.AddSingleton<SqliteFieldMaskingPolicyStore>();
        services.AddSingleton<SqliteApiKeyStore>();
        services.AddSingleton<SqliteAdminTokenStore>();
        services.AddSingleton<SqliteUserStore>();
        services.AddSingleton<SqliteRetentionPolicyStore>();
        services.AddSingleton<SqliteWorkspaceStore>();

        // Interface bindings — needed when SqliteWorkspaceStore is resolved (it depends on
        // these interfaces, not the concrete classes), and convenient for tests that work
        // through the abstraction.
        services.AddSingleton<ILogStore>(sp => sp.GetRequiredService<SqliteLogStore>());
        services.AddSingleton<ISpanStore>(sp => sp.GetRequiredService<SqliteSpanStore>());
        services.AddSingleton<ISavedQueryStore>(sp => sp.GetRequiredService<SqliteSavedQueryStore>());
        services.AddSingleton<IShareLinkStore>(sp => sp.GetRequiredService<SqliteShareLinkStore>());
        services.AddSingleton<IFieldMaskingPolicyStore>(sp => sp.GetRequiredService<SqliteFieldMaskingPolicyStore>());
        services.AddSingleton<IApiKeyAdmin>(sp => sp.GetRequiredService<SqliteApiKeyStore>());
        services.AddSingleton<IAdminTokenStore>(sp => sp.GetRequiredService<SqliteAdminTokenStore>());
        services.AddSingleton<IAdminTokenAdmin>(sp => sp.GetRequiredService<SqliteAdminTokenStore>());
        services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<SqliteUserStore>());
        services.AddSingleton<IRetentionPolicyStore>(sp => sp.GetRequiredService<SqliteRetentionPolicyStore>());
        services.AddSingleton<IWorkspaceStore>(sp => sp.GetRequiredService<SqliteWorkspaceStore>());
        return services.BuildServiceProvider();
    }
}
