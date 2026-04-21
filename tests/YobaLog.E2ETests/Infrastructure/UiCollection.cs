namespace YobaLog.E2ETests.Infrastructure;

// Single-Kestrel + single-Browser collection: every UI test class shares one fixture. Starting
// and tearing down 3-4 WebApplication + Chromium pairs per test run was racing startup (Kestrel
// port binding, Playwright IPC) and producing flaky timeouts on POST /Login. One shared
// fixture means ~1 cold start total; tests use unique workspace slugs to avoid data collisions.
[CollectionDefinition(nameof(UiCollection))]
public sealed class UiCollection : ICollectionFixture<WebAppFixture>;
