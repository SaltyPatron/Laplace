using Testcontainers.PostgreSql;
using Xunit;

namespace Laplace.Migrations.Tests;

/// <summary>
/// Shared per-class fixture that spins up a postgis/postgis:18 container,
/// exposes its connection string, and tears down on dispose. xUnit's
/// IAsyncLifetime semantics handle container startup/shutdown.
///
/// Per the testing standard — Testcontainers covers the DbUp + extension
/// install layer at unit-test grain. Substrate-level integration tests
/// (laplace extension functions, opclasses) run via pg_regress against
/// the deployed cluster, not Testcontainers (the laplace_geom +
/// laplace_substrate extensions aren't in the postgis image).
/// </summary>
public class PostgisContainerFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; }

    public PostgisContainerFixture()
    {
        Container = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:18-3.6")
            .WithDatabase("laplace_test")
            .WithUsername("laplace_admin")
            .WithPassword("laplace_test_password")
            .Build();
    }

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync() => await Container.StartAsync();
    public async Task DisposeAsync() => await Container.DisposeAsync();
}
