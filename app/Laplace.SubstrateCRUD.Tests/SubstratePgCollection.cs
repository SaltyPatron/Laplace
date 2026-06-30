using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[CollectionDefinition("substrate-pg")]
public sealed class SubstratePgCollection : ICollectionFixture<LocalPgFixture> { }

[CollectionDefinition("substrate-pg-writer-throughput")]
public sealed class WriterThroughputPgCollection : ICollectionFixture<LocalPgFixture> { }
