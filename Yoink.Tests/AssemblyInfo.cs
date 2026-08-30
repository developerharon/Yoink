using Xunit;

// SettingsServiceTests redirects the static, process-wide SettingsService.SettingsPath for the
// duration of each test — safe only if no two tests can do that at once. The suite is small enough
// that running it fully sequentially costs nothing worth trading away for parallelism.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
