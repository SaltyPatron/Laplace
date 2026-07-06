using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

internal static class FnLuSynsetBridgeIngest
{
    private static readonly Hash128 LuTypeId = EntityTypeRegistry.FrameNetLu;

    internal const string MultiWordNetVersion = SourceEntityIdConventions.MultiWordNetWnVersion;

    internal static IAsyncEnumerable<SubstrateChange> StreamAsync(
        string path,
        Hash128 source,
        string labelPrefix,
        int batchSize,
        string synsetVersion = "pwn30",
        long maxInputUnits = 0,
        ISubstrateReader? reader = null,
        DecomposerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= DecomposerOptions.ForWitness("FnLuSynsetBridge");
        if (maxInputUnits > 0)
            options = options with { MaxInputUnits = maxInputUnits };
        return CategoryCorrespondenceIngestSupport.RunAsync(
            EnumerateTabRecordsAsync(path, synsetVersion, maxInputUnits, ct),
            source, TC.AcademicCurated, labelPrefix, batchSize, reader, options, ct);
    }

    internal static IAsyncEnumerable<SubstrateChange> StreamWfnNativeAsync(
        string path,
        Hash128 source,
        string labelPrefix,
        int batchSize,
        string synsetVersion = "pwn30",
        long maxInputUnits = 0,
        ISubstrateReader? reader = null,
        DecomposerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= DecomposerOptions.ForWitness("FnLuSynsetBridge");
        if (maxInputUnits > 0)
            options = options with { MaxInputUnits = maxInputUnits };
        return CategoryCorrespondenceIngestSupport.RunAsync(
            EnumerateWfnNativeRecordsAsync(path, synsetVersion, maxInputUnits, ct),
            source, TC.AcademicCurated, labelPrefix, batchSize, reader, options, ct);
    }

    internal static async IAsyncEnumerable<CategoryCorrespondenceRecord> EnumerateTabRecordsAsync(
        string path,
        string synsetVersion,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        long rowsTotal = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0 || line[0] == '#') continue;

            if (!TryParseRow(line, out string? frame, out string? luName, out string? synRaw))
                continue;

            Hash128? synId = SourceEntityIdConventions.ResolveSynsetAnchor(synRaw, synsetVersion);
            if (synId is null) continue;

            string luKey = SourceEntityIdConventions.FrameNetLuKey(frame, luName);
            if (CategoryAnchor.Id(luKey) is null) continue;

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
            rowsTotal++;

            yield return new CategoryCorrespondenceRecord(luKey, LuTypeId, synId.Value);

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
        }
    }

    internal static async IAsyncEnumerable<CategoryCorrespondenceRecord> EnumerateWfnNativeRecordsAsync(
        string path,
        string synsetVersion,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        long rowsTotal = 0;
        string? currentFrame = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0 || line[0] == '#') continue;

            if (TryParseWfnNativeFrameHeader(line, out string frameName))
            {
                currentFrame = frameName;
                continue;
            }

            if (currentFrame is null ||
                !TryParseWfnNativeDataLine(line, out string lemma, out string pos, out string synRaw))
                continue;

            Hash128? synId = SourceEntityIdConventions.ResolveSynsetAnchor(synRaw, synsetVersion);
            if (synId is null) continue;

            string luName = PosSuffix(pos) is { Length: > 0 } sfx ? $"{lemma}.{sfx}" : lemma;
            string luKey = SourceEntityIdConventions.FrameNetLuKey(currentFrame, luName);
            if (CategoryAnchor.Id(luKey) is null) continue;

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
            rowsTotal++;

            yield return new CategoryCorrespondenceRecord(luKey, LuTypeId, synId.Value);

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
        }
    }

    internal static bool TryParseWfnNativeFrameHeader(string line, out string frame)
    {
        const string prefix = "Frame:";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            frame = "";
            return false;
        }
        frame = line[prefix.Length..].Trim();
        return frame.Length > 0;
    }

    internal static bool TryParseWfnNativeDataLine(
        string line, out string lemma, out string pos, out string synRaw)
    {
        lemma = "";
        pos = "";
        synRaw = "";

        var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length < 2) return false;

        int syn = -1;
        for (int i = 1; i < tok.Length; i++)
            if (IsOffsetPos(tok[i])) { syn = i; break; }
        if (syn < 1) return false;
        synRaw = tok[syn];

        string head = tok[syn - 1];
        int pipe = head.IndexOf('|');
        if (pipe > 0)
        {
            lemma = head[..pipe];
            pos = head[(pipe + 1)..];
        }
        else
        {
            pos = head;
            lemma = syn - 1 == 1 ? tok[0] : string.Join('_', tok[..(syn - 1)]);
        }
        return lemma.Length > 0 && pos.Length > 0;
    }

    private static bool IsOffsetPos(string s)
    {
        int dash = s.IndexOf('-');
        if (dash <= 0 || dash + 1 >= s.Length) return false;
        for (int i = 0; i < dash; i++)
            if (!char.IsDigit(s[i])) return false;
        return s[dash + 1] is 'n' or 'v' or 'a' or 's' or 'r';
    }

    internal static bool TryParseRow(string line, out string frame, out string luName, out string synRaw)
    {
        frame = "";
        luName = "";
        synRaw = "";
        var fields = line.Split('\t');
        if (fields.Length < 3) return false;

        frame = fields[0].Trim();
        if (frame.Length == 0) return false;

        if (fields.Length >= 4)
        {
            string lemma = fields[1].Trim();
            string pos = fields[2].Trim();
            synRaw = fields[3].Trim();
            if (lemma.Length == 0 || pos.Length == 0 || synRaw.Length == 0) return false;
            luName = PosSuffix(pos) is { Length: > 0 } sfx ? $"{lemma}.{sfx}" : lemma;
            return true;
        }

        luName = fields[1].Trim();
        synRaw = fields[2].Trim();
        return luName.Length > 0 && synRaw.Length > 0;
    }

    internal static async Task<long?> EstimateLineCountAsync(string path, CancellationToken ct)
    {
        long lines = 0;
        await foreach (var _ in ReadLinesAsync(path, ct))
            lines++;
        return lines > 0 ? lines : null;
    }

    private static string PosSuffix(string pos) => pos.Trim().ToLowerInvariant() switch
    {
        "n" or "noun" => "n",
        "v" or "verb" => "v",
        "a" or "adj" or "adjective" => "a",
        "r" or "adv" or "adverb" => "adv",
        "s" or "satellite" => "a",
        "idio" => "idio",
        _ => pos.Trim().ToLowerInvariant(),
    };

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }
}
