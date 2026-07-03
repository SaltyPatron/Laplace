using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.UD;

public static class UdConlluParser
{
    public static async IAsyncEnumerable<UdSentence> ParseSentencesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tokens = new List<UdToken>(48);
        var mwts = new List<UdMwt>(4);
        byte[]? textUtf8 = null;
        int maxId = 0;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span;
            if (line.IsEmpty)
            {
                if (tokens.Count > 0)
                    yield return new UdSentence(textUtf8, tokens.ToList(), mwts.ToList(), maxId);
                tokens.Clear(); mwts.Clear(); textUtf8 = null; maxId = 0;
                continue;
            }
            if (line[0] == (byte)'#')
            {
                int eq = line.IndexOf((byte)'=');
                if (eq > 0 && line[..eq].Trim((byte)' ').SequenceEqual("# text"u8))
                {
                    var raw = line[(eq + 1)..].Trim((byte)' ');
                    textUtf8 = raw.IsEmpty ? null : CopyUtf8Field(raw);
                }
                continue;
            }

            if (!TsvSpan.TryField(line, 0, out var id0Span)) continue;
            string id0 = System.Text.Encoding.UTF8.GetString(id0Span);

            if (id0.Contains('-'))
            {
                int dash = id0.IndexOf('-');
                if (int.TryParse(id0[..dash], out int st) && int.TryParse(id0[(dash + 1)..], out int en)
                    && TsvSpan.TryField(line, 1, out var mwtForm))
                    mwts.Add(new UdMwt(st, en, CopyUtf8Field(mwtForm.Trim((byte)' '))));
                continue;
            }
            bool isEmptyNode = id0.Contains('.');
            int id = 0;
            if (!isEmptyNode && !int.TryParse(id0, out id)) continue;

            if (!TsvSpan.TryField(line, 1, out var formSpan)) continue;
            formSpan = formSpan.Trim((byte)' ');
            if (formSpan.IsEmpty || formSpan.SequenceEqual("_"u8)) continue;
            var formUtf8 = CopyUtf8Field(formSpan);
            ReadOnlySpan<byte> lemmaSpan = TsvSpan.TryField(line, 2, out var ls) ? ls.Trim((byte)' ') : formSpan;
            if (lemmaSpan.IsEmpty || lemmaSpan.SequenceEqual("_"u8)) lemmaSpan = formSpan;
            var lemmaUtf8 = lemmaSpan.SequenceEqual(formSpan) ? formUtf8 : CopyUtf8Field(lemmaSpan);
            bool formLemmaSame = ReferenceEquals(formUtf8, lemmaUtf8)
                                 || formUtf8.AsSpan().SequenceEqual(lemmaUtf8);
            string upos = TsvSpan.TryField(line, 3, out var uposSpan)
                ? System.Text.Encoding.UTF8.GetString(uposSpan).Trim() : "";
            string xpos = TsvSpan.TryField(line, 4, out var xposSpan)
                ? System.Text.Encoding.UTF8.GetString(xposSpan).Trim() : "";
            string[] feats = TsvSpan.TryField(line, 5, out var featSpan) && !featSpan.SequenceEqual("_"u8)
                ? System.Text.Encoding.UTF8.GetString(featSpan).Split('|', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            int head = TsvSpan.TryField(line, 6, out var headSpan)
                && int.TryParse(System.Text.Encoding.UTF8.GetString(headSpan), out int h) ? h : 0;
            string deprel = TsvSpan.TryField(line, 7, out var depSpan)
                ? System.Text.Encoding.UTF8.GetString(depSpan).Trim() : "";
            string deps = TsvSpan.TryField(line, 8, out var depsSpan)
                ? System.Text.Encoding.UTF8.GetString(depsSpan).Trim() : "_";
            string misc = TsvSpan.TryField(line, 9, out var miscSpan)
                ? System.Text.Encoding.UTF8.GetString(miscSpan).Trim() : "_";

            if (!isEmptyNode && id > maxId) maxId = id;
            tokens.Add(new UdToken(isEmptyNode ? -1 : id, id0, formUtf8, lemmaUtf8, formLemmaSame,
                upos, xpos, feats, head, deprel, deps, misc));
        }
        if (tokens.Count > 0)
            yield return new UdSentence(textUtf8, tokens.ToList(), mwts.ToList(), maxId);
    }

    private static byte[] CopyUtf8Field(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return Array.Empty<byte>();
        return span.ToArray();
    }
}
