using System.Net;
using System.Net.Http.Json;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ResponseFieldContractTests
{
    private sealed class Factory(bool authenticated = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.PostConfigure<LaplaceAuthOptions>(options =>
            {
                options.Mode = "key";
                options.DevPrincipal = authenticated ? "response-field-test" : null;
            });
            services.PostConfigure<StripeBillingOptions>(options =>
            {
                TestBillingOptions.IsolateFromHostStripe(options);
                options.Bypass = true;
            });
        });
    }

    [Fact]
    public async Task ReturnsTheSelectedNativeReadBounds()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/explore/response-field?x=love&y=hate&fanout=64&nearest=8");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var field = await response.Content.ReadFromJsonAsync<ResponseFieldResponse>();
        Assert.NotNull(field);
        Assert.Equal("response.field", field.Object);
        Assert.Equal(2, field.Rounds); Assert.Equal(64, field.Fanout); Assert.Equal(8, field.Nearest);
    }

    [Theory]
    [InlineData("x=love")]
    [InlineData("x=love&y=hate&fanout=0")]
    [InlineData("x=love&y=hate&fanout=4097")]
    [InlineData("x=love&y=hate&nearest=-1")]
    [InlineData("x=love&y=hate&nearest=257")]
    public async Task RejectsMissingOperandsAndInvalidBounds(string query)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/explore/response-field?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RequiresAnAuthenticatedWorkspace()
    {
        await using var factory = new Factory(false);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/explore/response-field?x=love&y=hate");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
