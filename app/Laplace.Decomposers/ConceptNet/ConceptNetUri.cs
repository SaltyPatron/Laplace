using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.ConceptNet;


public static class ConceptNetUri
{
    public static bool TryParseLangAndTerm(
        ReadOnlySpan<byte> uri, out ReadOnlySpan<byte> lang, out ReadOnlySpan<byte> termUnderscored)
    {
        lang = default;
        termUnderscored = default;
        return TryParseConceptUri(uri, out lang, out termUnderscored, out _);
    }

    public static bool TryParseConceptUri(
        ReadOnlySpan<byte> uri,
        out ReadOnlySpan<byte> lang,
        out ReadOnlySpan<byte> termUnderscored,
        out char? pos,
        out ReadOnlySpan<byte> wnSuffix)
    {
        lang = default;
        termUnderscored = default;
        pos = null;
        wnSuffix = default;
        if (uri.Length < 5 || uri[0] != (byte)'/' || uri[1] != (byte)'c' || uri[2] != (byte)'/')
            return false;
        int i = 3;
        int langStart = i;
        while (i < uri.Length && uri[i] != (byte)'/') i++;
        if (i == langStart || i >= uri.Length) return false;
        lang = uri[langStart..i];
        i++;
        int termStart = i;
        int termEnd = uri[termStart..].IndexOf((byte)'/');
        termUnderscored = termEnd < 0 ? uri[termStart..] : uri[termStart..(termStart + termEnd)];
        if (termUnderscored.IsEmpty) return false;

        if (termEnd >= 0)
        {
            int posStart = termStart + termEnd + 1;
            if (posStart < uri.Length)
            {
                char c = (char)uri[posStart];
                if (c is 'n' or 'v' or 'a' or 'r' or 's')
                {
                    pos = c;
                    int afterPos = posStart + 1;
                    if (afterPos + 2 < uri.Length
                        && uri[afterPos] == (byte)'/'
                        && uri[afterPos + 1] == (byte)'w'
                        && uri[afterPos + 2] == (byte)'n')
                    {
                        int wnStart = afterPos + 3;
                        if (wnStart < uri.Length && uri[wnStart] == (byte)'/')
                            wnStart++;
                        if (wnStart < uri.Length)
                            wnSuffix = uri[wnStart..];
                    }
                }
            }
        }
        return true;
    }

    public static bool TryParseConceptUri(
        ReadOnlySpan<byte> uri,
        out ReadOnlySpan<byte> lang,
        out ReadOnlySpan<byte> termUnderscored,
        out char? pos) =>
        TryParseConceptUri(uri, out lang, out termUnderscored, out pos, out _);

    public static Hash128? ResolveSynsetFromWnSuffix(ReadOnlySpan<byte> wnSuffix, char? pos = null)
    {
        if (wnSuffix.IsEmpty) return null;
        string suffix = Encoding.UTF8.GetString(wnSuffix);
        Hash128? fromAnchor = SourceEntityIdConventions.ResolveSynsetAnchor(suffix);
        return fromAnchor ?? ConceptNetWnTopicMap.Resolve(suffix, pos);
    }

    public static Hash128? ResolveSynsetFromExternalUrl(ReadOnlySpan<byte> url) =>
        url.IsEmpty ? null : SourceEntityIdConventions.ResolveSynsetAnchor(Encoding.UTF8.GetString(url));

    public static bool IsExternalUrlRelation(ReadOnlySpan<byte> relationUri) =>
        relationUri.SequenceEqual("/r/ExternalURL"u8);

    public static bool TryAppendTerm(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> termUnderscored, Hash128 sourceId, out Hash128 rootId) =>
        ContentTierSpine.TryStageUnderscoredIntoBuilder(b, termUnderscored, sourceId, out rootId);

    /// <summary>
    /// Number of source records ConceptNet lists for an assertion (objects in its "sources"
    /// array), used as the record's observation count. Counts array elements, the corpus's
    /// own grain, without allocating; nested objects are not counted. Returns at least 1.
    /// </summary>
    public static long ParseSourceCount(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return 1;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName
                    || !reader.ValueTextEquals("sources"u8))
                    continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return 1;

                long count = 0;
                int depth = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (depth == 0) count++;
                        depth++;
                    }
                    else if (reader.TokenType == JsonTokenType.EndObject) depth--;
                    else if (reader.TokenType == JsonTokenType.EndArray && depth == 0) break;
                }
                return count > 0 ? count : 1;
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"ConceptNetUri: failed to parse sources from metadata JSON: {ex.Message}");
        }
        return 1;
    }

    /// <summary>ConceptNet's assertion "weight" from the metadata JSON column (1.0 when
    /// absent): the magnitude the record carries into the fold.</summary>
    public static double ParseWeight(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return 1.0;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("weight"u8))
                    return reader.Read() && reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : 1.0;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"ConceptNetUri: failed to parse weight from metadata JSON: {ex.Message}");
        }
        return 1.0;
    }
}
