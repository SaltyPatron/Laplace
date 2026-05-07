namespace Laplace.Pipeline;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// In-memory codepoint → entity_hash + position lookup. Loaded once at
/// startup from the SeedTableGenerator's seed_db_rows.tsv, so decomposers
/// never round-trip the database for tier-0 atom hashes.
///
/// If the seed TSV is missing or a codepoint is not present, falls back to
/// computing the entity_hash on the fly via <see cref="IIdentityHashing"/>
/// over the codepoint's UTF-8 bytes — keeps F1 functional in environments
/// where the seed has not been generated yet.
///
/// Phase 2 / Track D / D3.
/// </summary>
public sealed class CodepointPool : ICodepointPool
{
    private readonly Dictionary<int, AtomId> _codepointToHash = new();
    private readonly IIdentityHashing        _hashing;

    public CodepointPool(IIdentityHashing hashing)
    {
        _hashing = hashing;
    }

    public async Task LoadFromTsvAsync(string seedDbRowsTsvPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(seedDbRowsTsvPath))
        {
            return;
        }
        using var reader = new StreamReader(seedDbRowsTsvPath);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            // Format: hash_hex \t tier \t codepoint_int \t content_hex \t canonical_hash_hex
            var parts = line.Split('\t');
            if (parts.Length < 3) { continue; }
            var hashHex   = parts[0];
            var codepoint = int.Parse(parts[2], CultureInfo.InvariantCulture);
            _codepointToHash[codepoint] = AtomId.FromSpan(HexDecode(hashHex));
        }
    }

    public AtomId AtomIdFor(int codepoint)
    {
        if (_codepointToHash.TryGetValue(codepoint, out var hash))
        {
            return hash;
        }
        // Fallback: compute on-the-fly. Same algorithm the generator uses,
        // so the resulting hash matches what the seeded entity_tier0 row
        // would have. Adds a Dictionary entry so subsequent lookups are O(1).
        var utf8 = EncodeUtf8(codepoint);
        var computed = _hashing.AtomId(utf8);
        _codepointToHash[codepoint] = computed;
        return computed;
    }

    private static byte[] EncodeUtf8(int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return BitConverter.GetBytes(codepoint);
        }
        if (System.Text.Rune.IsValid(codepoint))
        {
            var rune = new System.Text.Rune(codepoint);
            Span<byte> buf = stackalloc byte[4];
            var written = rune.EncodeToUtf8(buf);
            return buf[..written].ToArray();
        }
        return new byte[]
        {
            (byte)((codepoint >> 24) & 0xFF),
            (byte)((codepoint >> 16) & 0xFF),
            (byte)((codepoint >>  8) & 0xFF),
            (byte)( codepoint        & 0xFF),
        };
    }

    private static byte[] HexDecode(string hex)
    {
        var n = hex.Length / 2;
        var result = new byte[n];
        for (int i = 0; i < n; ++i)
        {
            result[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return result;
    }
}
