using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.OMW;
using Xunit;

namespace Laplace.Decomposers.Tests.OMW;

public sealed class OMWLmfCoverageAuditTests
{
    private const string Estate = "/vault/Data/.refresh-20260903/OMW-2.0";
    [SkippableFact]
    public async Task All_32_Current_Lexicons_Parse_Without_Unknown_Fields_And_Reconcile_References()
    {
        Skip.IfNot(Directory.Exists(Estate), "staged OMW 2.0 estate is not mounted");
        IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(
            OMWLmfArtifacts.Build(Estate, DecomposerOptions.Default));
        IngestArtifact[] xmls = graph.Selected
            .Where(static artifact => artifact.MediaType == "application/xml")
            .OrderBy(static artifact => artifact.RelativePath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(32, xmls.Length);

        var lexicons = new List<LexiconCoverage>(xmls.Length);
        var packageLexicons = new HashSet<string>(StringComparer.Ordinal);
        var requiredLexicons = new HashSet<string>(StringComparer.Ordinal);
        var unknownRelations = new HashSet<string>(StringComparer.Ordinal);

        foreach (IngestArtifact artifact in xmls)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            var entries = new HashSet<string>(StringComparer.Ordinal);
            var senses = new HashSet<string>(StringComparer.Ordinal);
            var synsets = new HashSet<string>(StringComparer.Ordinal);
            var behaviours = new HashSet<string>(StringComparer.Ordinal);
            var memberRefs = new HashSet<string>(StringComparer.Ordinal);
            var senseRefs = new HashSet<string>(StringComparer.Ordinal);
            var synsetRefs = new HashSet<string>(StringComparer.Ordinal);
            var behaviourRefs = new HashSet<string>(StringComparer.Ordinal);
            string id = "";
            string language = "";
            string version = "";

            void Count(string name, long amount = 1) =>
                counts[name] = counts.GetValueOrDefault(name) + amount;

            await foreach (OmwLmfRecord record in OMWLmfParser.ReadAsync(
                               artifact.Path, artifact.FileLabel))
            {
                switch (record)
                {
                    case OmwLmfLexicon lexicon:
                        Count("lexicons");
                        id = lexicon.Id;
                        language = lexicon.LanguageCode;
                        version = lexicon.Version;
                        packageLexicons.Add(id);
                        if (lexicon.License.Length > 0) Count("license_fields");
                        if (lexicon.Citation.Length > 0) Count("citation_fields");
                        if (lexicon.Url.Length > 0) Count("url_fields");
                        break;
                    case OmwLmfRequires requires:
                        Count("requires");
                        requiredLexicons.Add(requires.Reference);
                        break;
                    case OmwLmfLexicalEntry entry:
                        Count("lexical_entries");
                        entries.Add(entry.Id);
                        Count("forms", entry.Forms.Count);
                        Count("form_tags", entry.Forms.Sum(static form => form.Tags.Count));
                        Count("senses", entry.Senses.Count);
                        foreach (OmwLmfSense sense in entry.Senses)
                        {
                            senses.Add(sense.Id);
                            synsetRefs.Add(sense.Synset);
                            if (sense.Count.Length > 0) Count("sense_counts");
                            if (sense.Identifier.Length > 0) Count("sense_identifiers");
                            if (sense.AdjectivePosition.Length > 0) Count("adjective_positions");
                            if (sense.Lexicalized.Length > 0) Count("sense_lexicalized_fields");
                            foreach (string frame in sense.Subcategorization.Split(
                                         ' ', StringSplitOptions.RemoveEmptyEntries))
                                behaviourRefs.Add(frame);
                            Count("sense_relations", sense.Relations.Count);
                            foreach (OmwLmfRelation relation in sense.Relations)
                            {
                                senseRefs.Add(relation.Target);
                                if (!OMWLmfEmitter.SupportsRelation(relation.Type))
                                    unknownRelations.Add(relation.Type);
                            }
                        }
                        break;
                    case OmwLmfSynset synset:
                        Count("synsets");
                        synsets.Add(synset.Id);
                        if (synset.Ili.Length > 0) Count("ili_references");
                        if (synset.Identifier.Length > 0) Count("synset_identifiers");
                        Count("member_references", synset.Members.Count);
                        foreach (string member in synset.Members) memberRefs.Add(member);
                        Count("definitions", synset.Definitions.Count);
                        Count("examples", synset.Examples.Count);
                        Count("synset_relations", synset.Relations.Count);
                        foreach (OmwLmfRelation relation in synset.Relations)
                        {
                            synsetRefs.Add(relation.Target);
                            if (!OMWLmfEmitter.SupportsRelation(relation.Type))
                                unknownRelations.Add(relation.Type);
                        }
                        break;
                    case OmwLmfSyntacticBehaviour behaviour:
                        Count("syntactic_behaviours");
                        behaviours.Add(behaviour.Id);
                        break;
                }
            }

            memberRefs.ExceptWith(senses);
            senseRefs.ExceptWith(senses);
            synsetRefs.ExceptWith(synsets);
            behaviourRefs.ExceptWith(behaviours);
            lexicons.Add(new LexiconCoverage(
                artifact.RelativePath, artifact.Bytes ?? 0, id, language, version, counts,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["member_to_sense"] = memberRefs.Count,
                    ["sense_relation_to_sense"] = senseRefs.Count,
                    ["sense_or_relation_to_synset"] = synsetRefs.Count,
                    ["sense_to_syntactic_behaviour"] = behaviourRefs.Count,
                }));
        }

        requiredLexicons.ExceptWith(packageLexicons);
        var proof = new CoverageProof(
            "OMW 2.0 WN-LMF 1.4",
            DateTimeOffset.UtcNow,
            graph.Artifacts.Count,
            graph.Selected.Count,
            graph.Artifacts.GroupBy(static artifact => artifact.DispositionName)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
            lexicons,
            unknownRelations.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            requiredLexicons.OrderBy(static value => value, StringComparer.Ordinal).ToArray());
        await File.WriteAllTextAsync(TestIngestPaths.Receipt("omw2-coverage.json"),
            JsonSerializer.Serialize(proof, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        Assert.Empty(unknownRelations);
        Assert.Empty(requiredLexicons);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (LexiconCoverage coverage in lexicons)
            foreach ((string name, long count) in coverage.Counts)
                totals[name] = totals.GetValueOrDefault(name) + count;
        var expectedReleaseCounts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["lexicons"] = 32,
            ["requires"] = 31,
            ["lexical_entries"] = 1_352_315,
            ["forms"] = 20_888,
            ["form_tags"] = 16_356,
            ["senses"] = 2_053_734,
            ["sense_counts"] = 35_400,
            ["sense_relations"] = 92_235,
            ["synsets"] = 1_192_770,
            ["ili_references"] = 1_192_770,
            ["definitions"] = 415_977,
            ["examples"] = 131_558,
            ["synset_relations"] = 285_348,
            ["syntactic_behaviours"] = 35,
            ["sense_lexicalized_fields"] = 810,
        };
        foreach ((string name, long expected) in expectedReleaseCounts)
            Assert.Equal(expected, totals.GetValueOrDefault(name));
        Assert.All(lexicons, coverage =>
        {
            Assert.Equal(1, coverage.Counts.GetValueOrDefault("lexicons"));
            Assert.True(coverage.Counts.GetValueOrDefault("lexical_entries") > 0);
            Assert.True(coverage.Counts.GetValueOrDefault("senses") > 0);
            Assert.True(coverage.Counts.GetValueOrDefault("synsets") > 0);
            Assert.All(coverage.UnresolvedReferences.Values, value => Assert.Equal(0L, value));
        });
    }

    private sealed record LexiconCoverage(
        string Path,
        long Bytes,
        string Id,
        string Language,
        string Version,
        IReadOnlyDictionary<string, long> Counts,
        IReadOnlyDictionary<string, long> UnresolvedReferences);

    private sealed record CoverageProof(
        string Recipe,
        DateTimeOffset MeasuredAtUtc,
        int PhysicalArtifacts,
        int SelectedArtifacts,
        IReadOnlyDictionary<string, int> Dispositions,
        IReadOnlyList<LexiconCoverage> Lexicons,
        IReadOnlyList<string> UnknownRelationTypes,
        IReadOnlyList<string> UnresolvedRequiredLexicons);
}
