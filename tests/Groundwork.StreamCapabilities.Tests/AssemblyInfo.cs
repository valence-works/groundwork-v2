using Xunit;

// Native capability proofs share the provider database supplied by the host. Their individual
// methods create their own concurrency, but running unrelated schema lifecycles in parallel makes
// failures depend on test scheduling rather than the capability under proof.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
