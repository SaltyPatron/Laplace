using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class EtlWitness : IGrammarWitness
{
    private readonly EtlSource _src;
    private readonly IGrammarWitness? _delegate;

    public EtlWitness(in EtlWitnessContext ctx)
    {
        _src = ctx.Source;
        _delegate = EtlWitnessFactory.TryCreate(ctx);
    }

    public string ModalityId => _src.Modality.GrammarId;

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (_delegate is not null)
        {
            _delegate.WalkRow(composed, ctx, b);
            return;
        }
        if (composed.Composer is null) return;

        var fields = composed.Composer.FieldSpans();
        ReadOnlySpan<byte> utf8 = composed.Utf8;

        foreach (var rule in _src.NodeEdgeMap)
        {
            if (rule.SubjectField >= fields.Count || rule.ObjectField >= fields.Count) continue;

            if (!ResolveField(b, utf8, fields[rule.SubjectField], rule.SubjectKind, out var subjectId))
                continue;
            if (!ResolveField(b, utf8, fields[rule.ObjectField], rule.ObjectKind, out var objectId))
                continue;

            b.AddAttestation(NativeAttestation.Categorical(
                subjectId, rule.RelationType, objectId, _src.SourceId, _src.Trust,
                contextId: ctx.ContextId));
        }
    }

    private bool ResolveField(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> utf8, (uint Start, uint End) span,
        EdgeRoleKind kind, out Hash128 id)
    {
        id = default;
        ReadOnlySpan<byte> value = utf8[(int)span.Start..(int)span.End];
        if (value.IsEmpty) return false;

        return kind switch
        {
            EdgeRoleKind.Content =>
                ContentTierSpine.TryStageIntoBuilder(b, Trim(value), _src.SourceId, out id),
            EdgeRoleKind.Anchor =>
                ResolveAnchor(b, value, out id),
            _ => false,
        };
    }

    private bool ResolveAnchor(SubstrateChangeBuilder b, ReadOnlySpan<byte> value, out Hash128 id)
    {
        id = default;
        string key = Encoding.UTF8.GetString(value).Trim();
        if (key.Length == 0) return false;

        Hash128? resolved = _src.Anchor switch
        {
            AnchorResolver.SenseKey => SenseAnchor.Emit(b, key, _src.SourceId, _src.Trust),
            AnchorResolver.FrameCategory => CategoryAnchor.Emit(b, key, EntityTypeRegistry.FrameNetFrame, _src.SourceId, _src.Trust),
            AnchorResolver.IliSynset => SourceEntityIdConventions.ResolveSynsetAnchor(key),
            _ => null,
        };
        if (resolved is null) return false;
        id = resolved.Value;
        return true;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        while (s.Length > 0 && s[0] == (byte)' ') s = s[1..];
        while (s.Length > 0 && s[^1] == (byte)' ') s = s[..^1];
        return s;
    }
}
