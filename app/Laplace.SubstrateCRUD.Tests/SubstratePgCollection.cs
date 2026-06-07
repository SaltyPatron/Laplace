using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[CollectionDefinition("substrate-pg")]
public sealed class SubstratePgCollection : ICollectionFixture<LocalPgFixture> { }
