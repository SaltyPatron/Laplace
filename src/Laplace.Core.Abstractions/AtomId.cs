namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// Content-addressed identity for any substrate entity (atom or composition,
/// any tier). Always 32 bytes (BLAKE3-256). Same content = same hash = same
/// entity row in the substrate, deduplicated across decomposers and modalities.
/// </summary>
public readonly record struct AtomId(ReadOnlyMemory<byte> Hash)
{
    public const int SizeBytes = 32;

    public static AtomId FromSpan(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SizeBytes)
        {
            throw new ArgumentException(
                $"AtomId requires exactly {SizeBytes} bytes; got {bytes.Length}.",
                nameof(bytes));
        }
        return new AtomId(bytes.ToArray());
    }

    public ReadOnlySpan<byte> AsSpan() => Hash.Span;

    public override string ToString() => Convert.ToHexString(Hash.Span);
}
