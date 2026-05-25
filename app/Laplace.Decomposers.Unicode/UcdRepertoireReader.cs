using System.IO.Compression;
using System.Xml;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Streams the <b>complete</b> per-codepoint property set from the UCD source
/// (UCDXML, UAX#42) — every attribute on each <c>&lt;char&gt;</c> /
/// <c>&lt;reserved&gt;</c> / <c>&lt;noncharacter&gt;</c> / <c>&lt;surrogate&gt;</c>
/// element, not a hand-picked subset. This is the authoritative source the
/// UnicodeDecomposer breaks down into the substrate; the runtime perf-cache
/// is a derived projection of a few hot properties, never the source of the
/// DB seed.
///
/// <para>The flat UCDXML carries the full resolved property set directly on
/// each element (no group inheritance), so each yielded record is complete on
/// its own. Range elements (<c>first-cp</c>/<c>last-cp</c>) are expanded to
/// one record per codepoint.</para>
/// </summary>
public sealed class UcdRepertoireReader
{
    private readonly string _zipPath;

    /// <param name="ucdxmlZipPath">Path to <c>ucd.nounihan.flat.zip</c> (or the
    /// all-flat zip once Unihan is folded in).</param>
    public UcdRepertoireReader(string ucdxmlZipPath)
    {
        _zipPath = ucdxmlZipPath ?? throw new ArgumentNullException(nameof(ucdxmlZipPath));
        if (!File.Exists(_zipPath))
            throw new FileNotFoundException($"UCDXML zip not found: {_zipPath}", _zipPath);
    }

    /// <summary>One codepoint with its full UCD property set. <see cref="Props"/>
    /// holds every UCDXML attribute verbatim (e.g. gc, sc, scx, blk, bc, ccc,
    /// lb, ea, age, dt, dm, nv, uc/lc/tc/cf, GCB/WB/SB/InCB, Emoji, …); the
    /// decomposer decides which become typed attestations vs. runtime-blob
    /// fields. Nothing is dropped at read time.</summary>
    public readonly record struct CodepointProps(uint Codepoint, IReadOnlyDictionary<string, string> Props)
    {
        public string? Get(string attr) => Props.TryGetValue(attr, out var v) ? v : null;
    }

    /// <summary>Streams every assigned + unassigned codepoint in the repertoire
    /// with its complete attribute set, in codepoint order within each element.</summary>
    public IEnumerable<CodepointProps> Read()
    {
        using var fs = File.OpenRead(_zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"no .xml entry in {_zipPath}");

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        bool inRepertoire = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "repertoire")
            {
                inRepertoire = true;
                continue;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "repertoire")
                break;
            if (!inRepertoire || reader.NodeType != XmlNodeType.Element)
                continue;

            string el = reader.LocalName;
            if (el is not ("char" or "reserved" or "noncharacter" or "surrogate"))
                continue;

            // Snapshot every attribute on this element.
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            uint? cp = null, firstCp = null, lastCp = null;
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    props[reader.LocalName] = reader.Value;
                    switch (reader.LocalName)
                    {
                        case "cp":       cp = ParseHex(reader.Value); break;
                        case "first-cp": firstCp = ParseHex(reader.Value); break;
                        case "last-cp":  lastCp = ParseHex(reader.Value); break;
                    }
                }
                reader.MoveToElement();
            }

            if (cp is uint single)
            {
                yield return new CodepointProps(single, props);
            }
            else if (firstCp is uint f && lastCp is uint l && l >= f)
            {
                for (uint c = f; c <= l; c++)
                    yield return new CodepointProps(c, props);
            }
        }
    }

    private static uint ParseHex(string s) => Convert.ToUInt32(s, 16);
}
