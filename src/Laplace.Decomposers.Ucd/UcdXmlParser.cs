namespace Laplace.Decomposers.Ucd;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

/// <summary>
/// Streaming parser for the canonical UCD XML release (UAX #42), e.g.
/// <c>ucd.all.flat.xml</c>. Yields one <see cref="UcdCodepointRecord"/> per
/// <c>&lt;char&gt;</c> element. Streaming = no full document load (the
/// `ucd.all.flat.xml` is ~219 MB at Unicode 17.0).
///
/// Phase 3 / Track E / E2.
///
/// The flat form has every char direct under &lt;repertoire&gt; with all
/// properties as attributes. The grouped form factors common attributes up
/// to a parent &lt;group&gt; element — this parser handles both by
/// merging group-level attributes into each child char's property bag.
/// </summary>
public sealed class UcdXmlParser
{
    private const string UcdNamespace = "http://www.unicode.org/ns/2003/ucd/1.0";

    public static IEnumerable<UcdCodepointRecord> Parse(string path)
    {
        using var stream = File.OpenRead(path);
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace        = true,
            IgnoreComments          = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing           = DtdProcessing.Ignore,
            CloseInput              = true,
        };
        using var reader = XmlReader.Create(stream, settings);

        Dictionary<string, string>? groupDefaults = null;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName == "group" &&
                    reader.NamespaceURI == UcdNamespace)
                {
                    groupDefaults = null;
                }
                continue;
            }
            if (reader.NamespaceURI != UcdNamespace)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "group":
                    groupDefaults = ReadAttributes(reader);
                    break;

                case "char":
                {
                    var record = ReadChar(reader, groupDefaults);
                    if (record is not null)
                    {
                        yield return record;
                    }
                    break;
                }

                /* UAX#42 also has <reserved>, <surrogate>, <noncharacter>
                 * elements that fill the codepoint space between assigned
                 * chars. Per CLAUDE.md invariant: substrate has rows for the
                 * full 1,114,112 codepoint slots so future Unicode versions
                 * slot in. Each yields a record with general_category set so
                 * downstream consumers (CanonicalOrdering, registries) treat
                 * the slot consistently. */
                case "reserved":
                case "surrogate":
                case "noncharacter":
                {
                    var record = ReadSpecialRange(reader, groupDefaults, reader.LocalName);
                    if (record is not null)
                    {
                        yield return record;
                    }
                    break;
                }
            }
        }
    }

    private static Dictionary<string, string> ReadAttributes(XmlReader reader)
    {
        var attrs = new Dictionary<string, string>(System.StringComparer.Ordinal);
        if (reader.HasAttributes)
        {
            for (var ok = reader.MoveToFirstAttribute(); ok; ok = reader.MoveToNextAttribute())
            {
                attrs[reader.LocalName] = reader.Value;
            }
            reader.MoveToElement();
        }
        return attrs;
    }

    private static UcdCodepointRecord? ReadChar(
        XmlReader reader,
        Dictionary<string, string>? groupDefaults)
    {
        var attrs = ReadAttributes(reader);

        if (groupDefaults is not null)
        {
            foreach (var kv in groupDefaults)
            {
                attrs.TryAdd(kv.Key, kv.Value);
            }
        }

        int firstCp, lastCp;
        if (attrs.TryGetValue("cp", out var cpHex))
        {
            firstCp = int.Parse(cpHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            lastCp  = firstCp;
        }
        else if (attrs.TryGetValue("first-cp", out var firstHex) &&
                 attrs.TryGetValue("last-cp",  out var lastHex))
        {
            firstCp = int.Parse(firstHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            lastCp  = int.Parse(lastHex,  NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            return null;
        }

        var aliases = new List<UcdNameAlias>(0);

        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName == "char" &&
                    reader.NamespaceURI == UcdNamespace)
                {
                    break;
                }
                if (reader.NodeType != XmlNodeType.Element ||
                    reader.NamespaceURI != UcdNamespace)
                {
                    continue;
                }

                if (reader.LocalName == "name-alias")
                {
                    var aliasAttr = reader.GetAttribute("alias") ?? string.Empty;
                    var typeAttr  = reader.GetAttribute("type")  ?? string.Empty;
                    aliases.Add(new UcdNameAlias(aliasAttr, typeAttr));
                }
                /* other child elements (reserved, surrogate, noncharacter, etc.)
                 * carry no per-codepoint properties beyond what the attributes
                 * already encode — skip silently. */
            }
        }

        return new UcdCodepointRecord(firstCp, lastCp, attrs, aliases);
    }

    private static UcdCodepointRecord? ReadSpecialRange(
        XmlReader reader,
        Dictionary<string, string>? groupDefaults,
        string elementName)
    {
        var attrs = ReadAttributes(reader);

        if (groupDefaults is not null)
        {
            foreach (var kv in groupDefaults)
            {
                attrs.TryAdd(kv.Key, kv.Value);
            }
        }

        int firstCp, lastCp;
        if (attrs.TryGetValue("cp", out var cpHex))
        {
            firstCp = int.Parse(cpHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            lastCp  = firstCp;
        }
        else if (attrs.TryGetValue("first-cp", out var firstHex) &&
                 attrs.TryGetValue("last-cp",  out var lastHex))
        {
            firstCp = int.Parse(firstHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            lastCp  = int.Parse(lastHex,  NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            return null;
        }

        /* Stamp the right general_category so downstream code sees the slot
         * for what it is. Surrogate codepoints get Cs; reserved/noncharacter
         * both get Cn (Unassigned/Other-Not-Assigned). The Unicode standard
         * treats noncharacters as a sub-class of Cn for general_category. */
        var gcOverride = elementName switch
        {
            "surrogate"    => "Cs",
            "noncharacter" => "Cn",
            _              => "Cn",   /* reserved */
        };
        attrs["gc"] = gcOverride;
        attrs.TryAdd("sc", "Zzzz");   /* Unknown script. */
        attrs["__ucd_kind"] = elementName;

        if (!reader.IsEmptyElement)
        {
            /* Skip any nested elements — reserved/surrogate/noncharacter
             * elements typically have no children, but be tolerant. */
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName == elementName &&
                    reader.NamespaceURI == UcdNamespace)
                {
                    break;
                }
            }
        }

        return new UcdCodepointRecord(firstCp, lastCp, attrs, new List<UcdNameAlias>(0));
    }
}
