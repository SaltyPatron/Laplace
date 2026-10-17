using System.Security.Cryptography;

namespace Laplace.Engine.Core;

/// <summary>
/// Envelope encryption for Mold-A-Model cartridges (<c>LPCART01</c> magic + 12-byte nonce +
/// 16-byte AES-256-GCM authentication tag + ciphertext). Cryptographically binds exported
/// GGUF cartridges whose vocabulary slots use <c>laplace-blake3-v1</c> content IDs.
/// </summary>
public static class FoundryCartridgeCrypto
{
    public static ReadOnlySpan<byte> Magic => "LPCART01"u8;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] EncryptCartridgeBytes(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != 32)
            throw new ArgumentException("Cartridge encryption key must be 32 bytes (256-bit AES-GCM).", nameof(key));

        var output = new byte[Magic.Length + NonceSize + TagSize + plaintext.Length];
        Magic.CopyTo(output);
        var nonce = output.AsSpan(Magic.Length, NonceSize);
        var tag = output.AsSpan(Magic.Length + NonceSize, TagSize);
        var ciphertext = output.AsSpan(Magic.Length + NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    public static byte[] DecryptCartridgeBytes(
        ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != 32)
            throw new ArgumentException("Cartridge decryption key must be 32 bytes (256-bit AES-GCM).", nameof(key));
        if (envelope.Length < Magic.Length + NonceSize + TagSize || !envelope[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid LPCART01 cartridge header.");

        var nonce = envelope.Slice(Magic.Length, NonceSize);
        var tag = envelope.Slice(Magic.Length + NonceSize, TagSize);
        var ciphertext = envelope[(Magic.Length + NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public static void EncryptFileInPlace(
        string path,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default)
    {
        byte[] plain = File.ReadAllBytes(path);
        byte[] encrypted = EncryptCartridgeBytes(plain, key, associatedData);
        File.WriteAllBytes(path, encrypted);
    }
}
