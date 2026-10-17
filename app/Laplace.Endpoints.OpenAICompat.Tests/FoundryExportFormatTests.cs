using System.Security.Cryptography;
using System.Text;
using Laplace.Endpoints.OpenAICompat;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The HTTP export service binds the GGUF synthesis command and advertises as writable
/// only formats with a complete export route, even when another native codec exists.
/// </summary>
public sealed class FoundryExportFormatTests
{
    [Fact]
    public void OnlyFormatsWithAnIntegratedExportPathAreWritable()
        => Assert.Equal(new[] { "gguf" }, CliFoundryExportService.WritableFormats.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("safetensors")]
    [InlineData("safetensor")]
    [InlineData("onnx")]
    [InlineData("pt")]
    public async Task UnwritableFormat_IsRefused_NotSilentlyMislabelled(string format)
    {
        var svc = new CliFoundryExportService();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.ExportAsync(
            recipeJson: null, recipeIdPrefix: "deadbeef", tokenizerDir: null,
            format: format, filename: null, ct: default));
        Assert.Contains(format, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gguf", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CartridgeCrypto_RoundTripsAndRejectsTampering()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] plain = Encoding.UTF8.GetBytes("GGUF-laplace-blake3-v1-test-payload");
        byte[] aad = Encoding.UTF8.GetBytes("recipe:deadbeef");

        byte[] envelope = FoundryCartridgeCrypto.EncryptCartridgeBytes(plain, key, aad);
        Assert.Equal("LPCART01", Encoding.ASCII.GetString(envelope.AsSpan(0, 8)));

        byte[] decrypted = FoundryCartridgeCrypto.DecryptCartridgeBytes(envelope, key, aad);
        Assert.Equal(plain, decrypted);

        // Tampering with ciphertext or wrong AAD fails authentication
        envelope[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() =>
            FoundryCartridgeCrypto.DecryptCartridgeBytes(envelope, key, aad));
    }

    [Fact]
    public void ComputeRoyaltyShares_NormalizesTo10000BpAndDistributesExactSeederPoolCents()
    {
        var quote = new BillingQuote(
            QuoteId: "q_royalty_test",
            Tenant: "buyer-tenant",
            ServiceId: "synthesis",
            Units: 10,
            AmountCents: 1000, // $10.00 -> 70% seeder pool = 700 cents
            Currency: "usd",
            Status: "consumed",
            StripeSessionId: null,
            StripeCheckoutUrl: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Consumed: true);

        var attributions = new[]
        {
            new RoyaltyAttributionShare("aaaa0000000000000000000000000001", 0, 30, 30.0, "seeder-alpha"),
            new RoyaltyAttributionShare("bbbb0000000000000000000000000002", 0, 70, 70.0, "seeder-beta"),
        };

        var royalties = BillingOrchestrator.ComputeRoyaltyShares(quote, attributions, DateTimeOffset.UtcNow);
        Assert.Equal(2, royalties.Count);
        Assert.Equal(10000, royalties.Sum(r => r.ShareBp));
        Assert.Equal(700, royalties.Sum(r => r.RoyaltyCents));
        Assert.Equal(3000, royalties[0].ShareBp);
        Assert.Equal(210, royalties[0].RoyaltyCents);
        Assert.Equal(7000, royalties[1].ShareBp);
        Assert.Equal(490, royalties[1].RoyaltyCents);
    }
}
