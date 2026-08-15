using Xunit;

// Browser profile names are configured once on TitleNormalizer at startup, which makes them
// process-wide state — and xUnit runs test classes in parallel by default, so a test that
// configures a profile was changing what an unrelated rollup test saw. The suite runs in a
// fraction of a second, so serialising it costs nothing and removes the whole class of
// cross-test interference rather than the one instance of it that happened to be noticed.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
