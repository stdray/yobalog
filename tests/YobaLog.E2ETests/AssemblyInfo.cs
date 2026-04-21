// Each IClassFixture<WebAppFixture> spins up its own Kestrel + Chromium. Running classes in
// parallel means 3+ browser+server pairs racing on startup — on a contended box the `Expect` after
// login times out (seen locally when dotnet test runs alongside YobaLog.Tests). Disable test-
// parallelization within this assembly; total runtime is still <5s with three classes.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
