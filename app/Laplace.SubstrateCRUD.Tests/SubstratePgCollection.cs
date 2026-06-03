using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// One shared PG fixture for every substrate test class. xUnit runs test
/// CLASSES in parallel, and per-class fixtures (IClassFixture) would each
/// dropdb/createdb the SAME <see cref="LocalPgFixture.DatabaseName"/> —
/// racing classes drop the database out from under each other. A collection
/// fixture serializes the member classes and provisions the DB exactly once.
/// </summary>
[CollectionDefinition("substrate-pg")]
public sealed class SubstratePgCollection : ICollectionFixture<LocalPgFixture> { }
