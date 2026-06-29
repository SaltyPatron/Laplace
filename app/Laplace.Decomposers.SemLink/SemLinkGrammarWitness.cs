using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

public enum SemLinkDocumentKind { PbVn, VnFn, PbWn, VnWn, FnWn, VnPbExternal }

internal sealed class SemLinkGrammarWitness(SemLinkDocumentKind kind) : IGrammarWitness
{
    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 FrameTypeId   = EntityTypeRegistry.FrameNetFrame;

    public string ModalityId => "json";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder builder)
    {
        switch (kind)
        {
            case SemLinkDocumentKind.PbVn: WalkPbVn(composed, builder); break;
            case SemLinkDocumentKind.VnFn: WalkVnFn(composed, builder); break;
            case SemLinkDocumentKind.PbWn: WalkCategoryToSynset(composed, builder, RolesetTypeId, NormalizeRolesetKey); break;
            case SemLinkDocumentKind.VnWn: WalkCategoryToSynset(composed, builder, VnClassTypeId, NormalizeVnClassKey); break;
            case SemLinkDocumentKind.FnWn: WalkCategoryToSynset(composed, builder, FrameTypeId, static k => k); break;
            case SemLinkDocumentKind.VnPbExternal: WalkVnPbExternal(composed, builder); break;
        }
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

    /// <summary>
    /// other_resources/external_vn2pb.json: VerbNet-class key (lemma-prefixed, e.g. <c>turn-26.6.1</c>,
    /// the canonical VerbNet class id surface, unlike vn-fn2's class-prefixed <c>26.6.1-turn</c>) to an
    /// array of PropBank roleset names. Same shape as <see cref="WalkVnFn"/>, pointed at PropBank
    /// rolesets instead of FrameNet frames, and additional to pb-vn2.json's roleset-keyed direction.
    /// </summary>
    private static void WalkVnPbExternal(in GrammarComposeContext ctx, SubstrateChangeBuilder b)
    {
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ctx.Ast);
        if (rootObj < 0) return;

        foreach (var (vnKeyNode, rolesetValueNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rootObj))
        {
            if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, vnKeyNode, out var vnKeySpan))
                continue;
            string vnClass = JsonGrammarHelper.Utf8ToString(vnKeySpan).Trim();
            if (vnClass.Length == 0) continue;

            var vnEntity = StageCategory(b, SemLinkDecomposer.NumericClassId(vnClass), VnClassTypeId);
            if (vnEntity is null) continue;

            if (!JsonGrammarHelper.IsArrayNode(ctx.Ast, rolesetValueNode)) continue;
            foreach (int rolesetNode in JsonGrammarHelper.StringNodesInArray(ctx.Ast, rolesetValueNode))
            {
                if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, rolesetNode, out var rolesetSpan))
                    continue;
                string roleset = JsonGrammarHelper.Utf8ToString(rolesetSpan).Trim();
                if (roleset.Length == 0) continue;
                var rsEntity = StageCategory(b, roleset, RolesetTypeId);
                if (rsEntity is null) continue;
                b.AddAttestation(NativeAttestation.Categorical(
                    vnEntity.Value, "CORRESPONDS_TO", rsEntity.Value, SemLinkDecomposer.Source, TC.AcademicCurated));
            }
        }
    }

    private static void WalkCategoryToSynset(
        in GrammarComposeContext ctx,
        SubstrateChangeBuilder b,
        Hash128 categoryTypeId,
        Func<string, string?> normalizeKey)
    {
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ctx.Ast);
        if (rootObj < 0) return;

        foreach (var (keyNode, valueNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rootObj))
        {
            if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, keyNode, out var keySpan))
                continue;
            string? key = normalizeKey(JsonGrammarHelper.Utf8ToString(keySpan).Trim());
            if (key is null || key.Length == 0) continue;

            var category = StageCategory(b, key, categoryTypeId);
            if (category is null) continue;

            foreach (string target in WnTargets(ctx, valueNode))
            {
                Hash128? synId = SourceEntityIdConventions.ResolveSynsetAnchor(target);
                if (synId is null) continue;
                b.AddAttestation(NativeAttestation.Categorical(
                    category.Value, "CORRESPONDS_TO", synId.Value, SemLinkDecomposer.Source, TC.AcademicCurated));
            }
        }
    }

    private static IEnumerable<string> WnTargets(GrammarComposeContext ctx, int valueNode)
    {
        if (JsonGrammarHelper.IsArrayNode(ctx.Ast, valueNode))
        {
            foreach (int item in JsonGrammarHelper.StringNodesInArray(ctx.Ast, valueNode))
            {
                if (JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, item, out var span))
                    yield return JsonGrammarHelper.Utf8ToString(span).Trim();
            }
            yield break;
        }

        if (JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, valueNode, out var single))
            yield return JsonGrammarHelper.Utf8ToString(single).Trim();
    }

    private static string? NormalizeRolesetKey(string key) => key.Length == 0 ? null : key;

    private static string? NormalizeVnClassKey(string key) =>
        key.Length == 0 ? null : SemLinkDecomposer.NumericClassId(key);

    private static Hash128? StageCategory(SubstrateChangeBuilder b, string key, Hash128 categoryTypeId)
    {
        Hash128? id = CategoryAnchor.Id(key);
        if (id is null) return null;
        b.AddEntity(new EntityRow(id.Value, EntityTier.Word, categoryTypeId, SemLinkDecomposer.Source));
        CategoryAnchor.AttestCategory(b, id.Value, categoryTypeId, SemLinkDecomposer.Source, TC.AcademicCurated);
        return id;
    }
}
