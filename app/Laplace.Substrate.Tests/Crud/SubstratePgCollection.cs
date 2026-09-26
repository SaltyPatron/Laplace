using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[CollectionDefinition("substrate-pg")]
public sealed class SubstratePgCollection : ICollectionFixture<LocalPgFixture> { }

// A throughput gate measures one writer alone. Parallel collections share this
// database and admit the same content (three-character text) concurrently; the
// ingest retry policy absorbs that race, a bare ApplyAsync does not.
[CollectionDefinition("substrate-pg-writer-throughput", DisableParallelization = true)]
public sealed class WriterThroughputPgCollection : ICollectionFixture<LocalPgFixture> { }
