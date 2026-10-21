using System.Net;
using System.Text;
using Laplace.Endpoints.OpenAICompat.Auth;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

[CollectionDefinition("Agent configuration writes", DisableParallelization = true)]
public sealed class AgentConfigWriteCollection;

[Collection("Agent configuration writes")]
public sealed class AgentConfigWriteTests
{
    [Fact]
    public async Task FileSystemFailureIsServiceFailureAndDoesNotClaimConfigurationWasSaved()
    {
        var work = Path.Combine(Path.GetTempPath(), "laplace-config-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var old = Environment.GetEnvironmentVariable("LAPLACE_AGENTS_CONFIG");
        try
        {
            var blockedParent = Path.Combine(work, "parent-is-a-file");
            await File.WriteAllTextAsync(blockedParent, "obstruction");
            Environment.SetEnvironmentVariable("LAPLACE_AGENTS_CONFIG", Path.Combine(blockedParent, "agents.json"));
            await using var factory = new GoldenFactory();
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, GoldenFactory.OperatorToken);
            using var response = await client.PutAsync("/v1/admin/agents/config", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("\"written\":true", await response.Content.ReadAsStringAsync());
            Assert.Equal("obstruction", await File.ReadAllTextAsync(blockedParent));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_AGENTS_CONFIG", old);
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task ConcurrentConfigurationWritesLeaveOneCompleteDocumentAndNoStagingFiles()
    {
        var work = Path.Combine(Path.GetTempPath(), "laplace-config-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var old = Environment.GetEnvironmentVariable("LAPLACE_AGENTS_CONFIG");
        var target = Path.Combine(work, "agents.json");
        const string first = "{\"default\":\"openai/gpt-4.1\"}";
        const string second = "{\"default\":\"openai/gpt-4.1-mini\"}";
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_AGENTS_CONFIG", target);
            await using var factory = new GoldenFactory();
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, GoldenFactory.OperatorToken);
            var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => client.PutAsync(
                "/v1/admin/agents/config", new StringContent(i % 2 == 0 ? first : second, Encoding.UTF8, "application/json"))));
            foreach (var reply in replies)
            {
                using (reply) Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            }
            Assert.Contains(await File.ReadAllTextAsync(target), new[] { first, second });
            Assert.Equal(new[] { target }, Directory.GetFiles(work));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_AGENTS_CONFIG", old);
            Directory.Delete(work, true);
        }
    }
}
