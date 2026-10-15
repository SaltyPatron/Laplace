using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PlatformOwnerPolicyTests
{
    internal const string GoogleClient = "owner-test-google-client";
    internal const string MicrosoftClient = "owner-test-microsoft-client";
    internal static readonly DateTimeOffset VerifiedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static JsonObject Document(string subject, string provider = "google") => new()
    {
        ["version"] = 1,
        ["owners"] = new JsonArray(new JsonObject
        {
            ["provider"] = provider,
            ["issuer"] = provider == "google" ? "https://accounts.google.com"
                : "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            ["subject"] = subject,
            ["client_id"] = provider == "google" ? GoogleClient : MicrosoftClient,
            ["verified_at"] = JsonSerializer.SerializeToNode(VerifiedAt),
            ["display_name"] = "Fixture owner", ["email"] = "fixture@example.test"
        })
    };

    internal static PlatformOwnerPolicy Policy(string subject) => Parse(Document(subject));

    internal static PlatformOwnerPolicy Parse(JsonObject document) => PlatformOwnerPolicy.Load(key => key switch
    {
        "LAPLACE_PLATFORM_OWNER_POLICY" => document.ToJsonString(),
        "LAPLACE_AUTH_GOOGLE_CLIENT_ID" => GoogleClient,
        "LAPLACE_AUTH_MICROSOFT_CLIENT_ID" => MicrosoftClient,
        _ => null
    });

    [Fact]
    public void AbsentAndExplicitEmptyPolicyHaveNoOwners()
    {
        Assert.Empty(PlatformOwnerPolicy.Load(_ => null).Owners);
        Assert.Empty(Parse(new JsonObject { ["version"] = 1, ["owners"] = new JsonArray() }).Owners);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    public void BindingMatchesOnlyTheExactValidatedTuple(string provider)
    {
        var policy = Parse(Document("fixture-subject", provider));
        var owner = Assert.Single(policy.Owners);
        Assert.Equal(VerifiedAt, owner.VerifiedAt);
        Assert.True(policy.Matches(owner.Provider, owner.Issuer, owner.Subject));
        Assert.False(policy.Matches(owner.Provider, owner.Issuer, "another-client-pairwise-subject"));
        Assert.False(policy.Matches(owner.Provider, owner.Issuer + "/", owner.Subject));
        Assert.False(policy.Matches("other", owner.Issuer, owner.Subject));
        Assert.False(policy.Matches(owner.Provider, owner.Issuer, owner.Email!));
    }

    [Theory]
    [InlineData("provider", "unsupported")]
    [InlineData("issuer", "https://attacker.example.test")]
    [InlineData("subject", "")]
    [InlineData("subject", " padded ")]
    [InlineData("client_id", "another-client")]
    [InlineData("verified_at", "not-a-time")]
    [InlineData("verified_at", "2025-01-01T00:00:00")]
    [InlineData("verified_at", "2999-01-01T00:00:00Z")]
    [InlineData("role", "administrator")]
    public void InvalidPoliciesAreRejectedWithoutIncludingPrivateValues(string field, string value)
    {
        var document = Document("private-fixture-subject");
        document["owners"]![0]![field] = value;
        var error = Assert.Throws<InvalidOperationException>(() => Parse(document));
        Assert.DoesNotContain("private-fixture-subject", error.Message);
        Assert.DoesNotContain("fixture@example.test", error.Message);
    }

    [Fact]
    public void DuplicateBindingsAndDuplicateJsonFieldsAreRejected()
    {
        var document = Document("fixture-subject");
        ((JsonArray)document["owners"]!).Add(document["owners"]![0]!.DeepClone());
        Assert.Throws<InvalidOperationException>(() => Parse(document));
        Assert.Throws<InvalidOperationException>(() => PlatformOwnerPolicy.Load(key =>
            key == "LAPLACE_PLATFORM_OWNER_POLICY" ? "{\"version\":1,\"version\":1,\"owners\":[]}" : null));
    }

    [Fact]
    public void MicrosoftPairwiseSubjectRequiresTheRecordedClientRegistration()
    {
        var document = Document("pairwise-subject", "microsoft");
        Assert.Throws<InvalidOperationException>(() => PlatformOwnerPolicy.Load(key => key switch
        {
            "LAPLACE_PLATFORM_OWNER_POLICY" => document.ToJsonString(),
            "LAPLACE_AUTH_MICROSOFT_CLIENT_ID" => "changed-client-registration",
            _ => null
        }));
    }

    private static PlatformOwnerPolicy FilePolicy(string path, string? json = null) => PlatformOwnerPolicy.Load(key => key switch
    {
        "LAPLACE_PLATFORM_OWNER_POLICY_FILE" => path,
        "LAPLACE_PLATFORM_OWNER_POLICY" => json,
        "LAPLACE_AUTH_GOOGLE_CLIENT_ID" => GoogleClient,
        "LAPLACE_AUTH_MICROSOFT_CLIENT_ID" => MicrosoftClient,
        _ => null
    });

    [Fact]
    public void ExplicitFileAndEquivalentInlinePolicyAreAccepted()
    {
        var path = Path.GetTempFileName();
        try
        {
            var document = Document("file-fixture");
            File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Assert.Single(FilePolicy(path).Owners);
            Assert.Single(FilePolicy(path, document.ToJsonString()).Owners);
            File.WriteAllText(path, "{\"version\":1,\"owners\":[]}");
            Assert.Empty(FilePolicy(path).Owners);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConflictingMissingAndRelativePolicyFilesFailClosed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Document("file-fixture").ToJsonString());
            Assert.Throws<InvalidOperationException>(() => FilePolicy(path, Document("different-fixture").ToJsonString()));
            File.Delete(path);
            var error = Assert.Throws<InvalidOperationException>(() => FilePolicy(path));
            Assert.DoesNotContain(path, error.Message);
            Assert.Throws<InvalidOperationException>(() => FilePolicy("relative-owner-policy.json"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OversizedInvalidUtf8AndMalformedPolicyFilesFailWithoutPrivateOutput()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var bytes in new[] { new byte[65537], new byte[] { 0xff, 0xfe }, System.Text.Encoding.UTF8.GetBytes("private-fixture-subject") })
            {
                File.WriteAllBytes(path, bytes);
                var error = Assert.Throws<InvalidOperationException>(() => FilePolicy(path));
                Assert.DoesNotContain(path, error.Message);
                Assert.DoesNotContain("private-fixture-subject", error.Message);
            }
        }
        finally { File.Delete(path); }
    }
}
