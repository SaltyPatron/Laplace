using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Lowers one immutable semantic recipe to the native streaming engine's RCP1
/// instruction image. Resolution happens once per recipe, before any source records
/// cross the native boundary.
/// </summary>
public static class NativeRecipeCompiler
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Compile(SemanticSourceRecipe recipe, int recordDepth = 2)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recordDepth is < 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(recordDepth));

        var aliases = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        foreach (var pair in recipe.ValueAliases.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            int separator = pair.Key.IndexOf('\0');
            if (separator < 0)
                throw new InvalidDataException("Recipe value alias has no property separator.");
            string property = pair.Key[..separator];
            if (!aliases.TryGetValue(property, out var values))
                aliases.Add(property, values = []);
            values.Add(new(pair.Key[(separator + 1)..], pair.Value));
        }

        using var image = new MemoryStream();
        using var writer = new BinaryWriter(image, Utf8, leaveOpen: true);
        writer.Write(recipe.DelimitedSyntax is null ? 0x31504352u : 0x32504352u);
        writer.Write((uint)recordDepth);
        if (recipe.DelimitedSyntax is { } syntax)
        {
            writer.Write(1u); // Native delimited provider; shared field/route instructions follow.
            WriteText(writer, syntax.RecordName);
            WriteText(writer, syntax.NamespaceUri);
            WriteText(writer, syntax.Separator);
            WriteText(writer, syntax.CommentPrefix);
            writer.Write(syntax.TrimFields ? 1u : 0u);
            WriteText(writer, syntax.DirectivePrefix);
            WriteText(writer, syntax.DirectiveRecordName);
            writer.Write(checked((uint)syntax.Columns.Count));
            foreach (string column in syntax.Columns) WriteText(writer, column);
            writer.Write(checked((uint)(syntax.DirectiveColumns?.Count ?? 0)));
            foreach (string column in syntax.DirectiveColumns ?? []) WriteText(writer, column);
            WriteText(writer, syntax.RangeColumn);
            WriteText(writer, syntax.RangeSeparator);
            WriteText(writer, syntax.RangeFirstField);
            WriteText(writer, syntax.RangeLastField);
            writer.Write(checked((uint)syntax.MinimumColumns));
            writer.Write(syntax.AllowTrailingEmptyColumn ? 1u : 0u);
        }
        writer.Write(checked((uint)recipe.Fields.Count));
        foreach (SourceRecipeField field in recipe.Fields)
        {
            if (!Enum.IsDefined(field.ValueKind) || !Enum.IsDefined(field.ReferenceCodec))
                throw new InvalidDataException($"Unknown native recipe opcode at '{field.SyntaxPath}'.");

            bool testimony = field.Disposition.HasFlag(SourceFieldDisposition.Testimony);
            var relation = testimony
                ? RelationTypeRegistry.Resolve(field.RelationName ?? field.PropertyName)
                : default;
            double rank = field.RelationRank ?? (testimony ? relation.Rank : 1);
            if (!double.IsFinite(rank) || rank is < 0 or > 1)
                throw new InvalidDataException($"Invalid relation rank at '{field.SyntaxPath}'.");
            Hash128 parent = !testimony ? Hash128.Zero : field.RelationParent is { } parentName
                ? RelationTypeRegistry.Resolve(parentName).Id : relation.ParentId ?? Hash128.Zero;
            Hash128 lexical = Hash128.Zero;
            if (testimony && field.PreserveLexicalValue)
            {
                if (string.IsNullOrEmpty(field.LexicalRelationName))
                    throw new InvalidDataException($"Missing lexical relation at '{field.SyntaxPath}'.");
                lexical = RelationTypeRegistry.Resolve(field.LexicalRelationName).Id;
            }

            WriteText(writer, field.SyntaxPath);
            writer.Write((uint)field.ValueKind);
            writer.Write((uint)field.Disposition);
            writer.Write((uint)field.ReferenceCodec);
            WriteText(writer, field.AbsentSentinel);
            WriteText(writer, field.SequenceSeparator);
            WriteText(writer, field.ObjectNamespace ?? $"recipe/value/{field.PropertyName}");
            WriteHash(writer, relation.Id);
            WriteHash(writer, parent);
            WriteHash(writer, EntityTypeRegistry.Id(field.ObjectEntityType));
            WriteHash(writer, lexical);
            writer.Write(rank);
            WriteAliases(writer, aliases, field.ValueAliasProperty ?? field.PropertyName);
        }

        writer.Write(checked((uint)recipe.ProviderRoutes.Count));
        foreach (SourceRecipeProviderRoute route in recipe.ProviderRoutes)
        {
            SourceRecipeSubjectBinding subject = route.Subject;
            if (!Enum.IsDefined(subject.Kind))
                throw new InvalidDataException($"Unknown subject binding in route '{route.RecordName}'.");
            if (subject.Kind == SourceSubjectBindingKind.ClassifierField
                && string.IsNullOrEmpty(subject.EntityNamespace))
                throw new InvalidDataException($"Classifier route '{route.RecordName}' has no entity namespace.");
            recipe.TryField($"{route.FieldPrefix}/@{subject.IdentityField}", out var identity);
            SourceReferenceCodec subjectCodec = identity?.ReferenceCodec ?? SourceReferenceCodec.None;
            if (subjectCodec == SourceReferenceCodec.None)
                subjectCodec = identity?.ValueKind switch
                {
                    SourceValueKind.Codepoint => SourceReferenceCodec.UnicodeCodepoint,
                    SourceValueKind.CodepointSequence => SourceReferenceCodec.UnicodeCodepointSequence,
                    _ => SourceReferenceCodec.None,
                };
            if (subjectCodec == SourceReferenceCodec.UPlusCodepointWithQualifier)
                throw new InvalidDataException($"Route '{route.RecordName}' needs one subject, not qualified reference targets.");

            WriteText(writer, route.RecordName);
            WriteText(writer, route.NamespaceUri);
            WriteText(writer, route.FieldPrefix);
            writer.Write((uint)subject.Kind);
            WriteText(writer, subject.IdentityField);
            WriteText(writer, subject.RangeStartField);
            WriteText(writer, subject.LastField);
            WriteText(writer, subject.EntityNamespace);
            WriteText(writer, subject.SequenceSeparator ?? identity?.SequenceSeparator ?? " ");
            writer.Write((uint)subjectCodec);
            WriteHash(writer, EntityTypeRegistry.Id(subject.EntityType));
            writer.Write(checked((uint)(route.ChildFieldPrefixes?.Count ?? 0)));
            if (route.ChildFieldPrefixes is { } children)
                foreach (var child in children.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    WriteText(writer, child.Key);
                    WriteText(writer, child.Value);
                }

            string? membership = route.RangeRelationName ?? route.RangeRelationProperty;
            bool hasMembershipRange = !string.IsNullOrEmpty(route.RangeStartField)
                || !string.IsNullOrEmpty(route.RangeEndField);
            if (membership is null && hasMembershipRange)
                throw new InvalidDataException($"Range-membership route '{route.RecordName}' has no relation declaration.");
            if (membership is not null && (subject.Kind == SourceSubjectBindingKind.CodepointRange
                || string.IsNullOrEmpty(route.RangeStartField) || string.IsNullOrEmpty(route.RangeEndField)))
                throw new InvalidDataException($"Range-membership route '{route.RecordName}' has incomplete subject/range declarations.");
            WriteText(writer, route.RangeStartField);
            WriteText(writer, route.RangeEndField);
            WriteHash(writer, membership is null ? Hash128.Zero : RelationTypeRegistry.Resolve(membership).Id);

            string property = identity is not null
                ? identity.ValueAliasProperty ?? identity.PropertyName
                : recipe.CanonicalProperty(subject.IdentityField);
            WriteAliases(writer, aliases, property);
        }

        writer.Flush();
        return image.ToArray();
    }

    private static void WriteText(BinaryWriter writer, string? value)
    {
        byte[] bytes = Utf8.GetBytes(value ?? "");
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static void WriteHash(BinaryWriter writer, Hash128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.WriteBytes(bytes);
        writer.Write(bytes);
    }

    private static void WriteAliases(BinaryWriter writer,
        Dictionary<string, List<KeyValuePair<string, string>>> aliases, string property)
    {
        aliases.TryGetValue(property, out var values);
        writer.Write(checked((uint)(values?.Count ?? 0)));
        if (values is null) return;
        foreach (var pair in values)
        {
            WriteText(writer, pair.Key);
            WriteText(writer, pair.Value);
        }
    }
}
