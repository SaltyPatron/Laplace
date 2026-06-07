using Testcontainers.PostgreSql;
using Xunit;

namespace Laplace.Migrations.Tests;

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
