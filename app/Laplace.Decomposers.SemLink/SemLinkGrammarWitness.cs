using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

internal enum SemLinkDocumentKind { PbVn, VnFn }

internal sealed class SemLinkGrammarWitness(SemLinkDocumentKind kind) : IGrammarWitness
{
    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 FrameTypeId   = EntityTypeRegistry.FrameNetFrame;

    public string ModalityId => "json";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder builder)
    {
        if (kind == SemLinkDocumentKind.PbVn)
            WalkPbVn(composed, builder);
        else
            WalkVnFn(composed, builder);
    }

    private static void WalkPbVn(in GrammarComposeContext ctx, SubstrateChangeBuilder b)
    {
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ctx.Ast);
        if (rootObj < 0) return;

        foreach (var (rolesetKeyNode, vnObjNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rootObj))
        {
            if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, rolesetKeyNode, out var rolesetKeySpan))
                continue;
            string rolesetKey = JsonGrammarHelper.Utf8ToString(rolesetKeySpan).Trim();
            if (rolesetKey.Length == 0 || !JsonGrammarHelper.IsObjectNode(ctx.Ast, vnObjNode)) continue;

            var rsEntity = StageCategory(b, rolesetKey, RolesetTypeId);
            if (rsEntity is null) continue;

            foreach (var (vnKeyNode, rolesObjNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, vnObjNode))
            {
                if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, vnKeyNode, out var vnKeySpan))
                    continue;
                string vnClass = JsonGrammarHelper.Utf8ToString(vnKeySpan).Trim();
                if (vnClass.Length == 0) continue;

                var vnEntity = StageCategory(b, SemLinkDecomposer.NumericClassId(vnClass), VnClassTypeId);
                if (vnEntity is null) continue;

                b.AddAttestation(NativeAttestation.Categorical(
                    rsEntity.Value, "CORRESPONDS_TO", vnEntity.Value, SemLinkDecomposer.Source, TC.AcademicCurated));

                if (!JsonGrammarHelper.IsObjectNode(ctx.Ast, rolesObjNode)) continue;
                foreach (var (argKeyNode, thetaNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rolesObjNode))
                {
                    if (!JsonGrammarHelper.TryComposedNode(ctx, argKeyNode, out var argId))
                        continue;
                    if (!JsonGrammarHelper.TryComposedNode(ctx, thetaNode, out var thetaId))
                        continue;
                    b.AddAttestation(NativeAttestation.Categorical(
                        argId, "ROLE_CORRESPONDS_TO", thetaId, SemLinkDecomposer.Source, TC.AcademicCurated,
                        contextId: vnEntity.Value));
                }
            }
        }
    }

    private static void WalkVnFn(in GrammarComposeContext ctx, SubstrateChangeBuilder b)
    {
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ctx.Ast);
        if (rootObj < 0) return;

        foreach (var (vnKeyNode, frameValueNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rootObj))
        {
            if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, vnKeyNode, out var vnKeySpan))
                continue;
            string vnClass = SemLinkDecomposer.VnClassFromKey(JsonGrammarHelper.Utf8ToString(vnKeySpan).Trim());
            if (vnClass.Length == 0) continue;

            var vnEntity = StageCategory(b, SemLinkDecomposer.NumericClassId(vnClass), VnClassTypeId);
            if (vnEntity is null) continue;

            if (!JsonGrammarHelper.IsArrayNode(ctx.Ast, frameValueNode)) continue;
            foreach (int frameNode in JsonGrammarHelper.StringNodesInArray(ctx.Ast, frameValueNode))
            {
                if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, frameNode, out var frameSpan))
                    continue;
                string frame = JsonGrammarHelper.Utf8ToString(frameSpan).Trim();
                if (frame.Length == 0) continue;
                var fnEntity = StageCategory(b, frame, FrameTypeId);
                if (fnEntity is null) continue;
                b.AddAttestation(NativeAttestation.Categorical(
                    vnEntity.Value, "CORRESPONDS_TO", fnEntity.Value, SemLinkDecomposer.Source, TC.AcademicCurated));
            }
        }
    }

    private static Hash128? StageCategory(SubstrateChangeBuilder b, string key, Hash128 categoryTypeId)
    {
        Hash128? id = CategoryAnchor.Id(key);
        if (id is null) return null;
        b.AddEntity(new EntityRow(id.Value, EntityTier.Vocabulary, categoryTypeId, SemLinkDecomposer.Source));
        CategoryAnchor.AttestCategory(b, id.Value, categoryTypeId, SemLinkDecomposer.Source, TC.AcademicCurated);
        return id;
    }
}
