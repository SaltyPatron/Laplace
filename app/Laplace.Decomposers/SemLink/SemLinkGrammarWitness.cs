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
    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;

    public string ModalityId => "json";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder builder)
    {
        switch (kind)
        {
            case SemLinkDocumentKind.PbVn: WalkPbVn(composed, builder, "pb-vn2"); break;
            case SemLinkDocumentKind.VnFn: WalkVnFn(composed, builder, "vn-fn2"); break;
            case SemLinkDocumentKind.PbWn: WalkCategoryToSynset(composed, builder, RolesetTypeId, NormalizeRolesetKey, "pb-wn"); break;
            case SemLinkDocumentKind.VnWn: WalkCategoryToSynset(composed, builder, VnClassTypeId, NormalizeVnClassKey, "vn-wn"); break;
            case SemLinkDocumentKind.FnWn: WalkCategoryToSynset(composed, builder, FrameTypeId, static k => k, "fn-wn"); break;
            case SemLinkDocumentKind.VnPbExternal: WalkVnPbExternal(composed, builder, "external_vn2pb"); break;
        }
    }

    private static void WalkPbVn(in GrammarComposeContext ctx, SubstrateChangeBuilder b, string predicate)
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

                var vnEntity = StageCategory(b, SourceEntityIdConventions.NumericVerbNetClassId(vnClass), VnClassTypeId);
                if (vnEntity is null) continue;

                b.AddAttestation(NativeAttestation.Categorical(
                    rsEntity.Value, predicate, vnEntity.Value, SemLinkDecomposer.Source, TC.AcademicCurated));

                if (!JsonGrammarHelper.IsObjectNode(ctx.Ast, rolesObjNode)) continue;
                foreach (var (argKeyNode, thetaNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rolesObjNode))
                {
                    if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, argKeyNode, out var argSpan))
                        continue;
                    if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, thetaNode, out var thetaSpan))
                        continue;
                    string arg = JsonGrammarHelper.Utf8ToString(argSpan).Trim();
                    string theta = JsonGrammarHelper.Utf8ToString(thetaSpan).Trim();
                    Hash128? argId = RoleAnchor.Declare(
                        b, RoleIdentityKind.PropBank, rsEntity.Value, ContentFloor(rolesetKey), arg,
                        EntityTypeRegistry.PropBankRole, SemLinkDecomposer.Source);
                    Hash128? thetaId = RoleAnchor.Declare(
                        b, RoleIdentityKind.VerbNet, vnEntity.Value, ContentFloor(vnClass), theta,
                        EntityTypeRegistry.VerbNetRole, SemLinkDecomposer.Source);
                    if (argId is null || thetaId is null) continue;
                    b.AddAttestation(NativeAttestation.Categorical(
                        argId.Value, predicate, thetaId.Value,
                        SemLinkDecomposer.Source, TC.AcademicCurated));
                }
            }
        }
    }

    private static void WalkVnFn(in GrammarComposeContext ctx, SubstrateChangeBuilder b, string predicate)
    {
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ctx.Ast);
        if (rootObj < 0) return;

        foreach (var (vnKeyNode, frameValueNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rootObj))
        {
            if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, vnKeyNode, out var vnKeySpan))
                continue;
            string vnClass = SemLinkDecomposer.VnClassFromKey(JsonGrammarHelper.Utf8ToString(vnKeySpan).Trim());
            if (vnClass.Length == 0) continue;

            var vnEntity = StageCategory(b, SourceEntityIdConventions.NumericVerbNetClassId(vnClass), VnClassTypeId);
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
                    vnEntity.Value, predicate, fnEntity.Value, SemLinkDecomposer.Source, TC.AcademicCurated));
            }
        }
    }

    private static void WalkVnPbExternal(in GrammarComposeContext ctx, SubstrateChangeBuilder b, string predicate)
    {
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ctx.Ast);
        if (rootObj < 0) return;

        foreach (var (vnKeyNode, rolesetValueNode) in JsonGrammarHelper.EnumerateObjectPairs(ctx.Ast, rootObj))
        {
            if (!JsonGrammarHelper.TryKeyUtf8(ctx.Ast, ctx.Utf8, vnKeyNode, out var vnKeySpan))
                continue;
            string vnClass = JsonGrammarHelper.Utf8ToString(vnKeySpan).Trim();
            if (vnClass.Length == 0) continue;

            var vnEntity = StageCategory(b, SourceEntityIdConventions.NumericVerbNetClassId(vnClass), VnClassTypeId);
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
                    vnEntity.Value, predicate, rsEntity.Value, SemLinkDecomposer.Source, TC.AcademicCurated));
            }
        }
    }

    private static void WalkCategoryToSynset(
        in GrammarComposeContext ctx,
        SubstrateChangeBuilder b,
        Hash128 categoryTypeId,
        Func<string, string?> normalizeKey,
        string predicate)
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
                    category.Value, predicate, synId.Value, SemLinkDecomposer.Source, TC.AcademicCurated));
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
        key.Length == 0 ? null : SourceEntityIdConventions.NumericVerbNetClassId(key);

    private static byte ContentFloor(string? value) =>
        value is not null && CompositionLaw.TryFace(value, out CompositionLaw.Face face)
            ? face.Tier
            : (byte)0;

    private static Hash128? StageCategory(SubstrateChangeBuilder b, string key, Hash128 categoryTypeId)
    {
        return AnchorAdmission.Emit(
            b, key, categoryTypeId, SemLinkDecomposer.Source, TC.AcademicCurated);
    }
}
