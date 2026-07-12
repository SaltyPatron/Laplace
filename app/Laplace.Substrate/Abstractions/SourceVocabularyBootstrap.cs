using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class SourceVocabularyBootstrap
{
    /// <summary>
    /// Expand declared relation names through the parent chain (family_root).
    /// Declaring a child pulls every ancestor root so the native attestation path
    /// never faults on an undeclared family member (HAS_POS class).
    /// </summary>
    public static IReadOnlyList<string> ExpandRelationsWithFamily(IEnumerable<string> relationNodeNames)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in relationNodeNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            AddWithAncestors(set, name);
        }
        return set.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// True when <paramref name="emittedCanonical"/>'s family root is covered by the
    /// declared set (exact match or shared ultimate parent / self-as-root).
    /// </summary>
    public static bool DeclaredCoversEmitted(
        IEnumerable<string> declaredRelationNames,
        string emittedCanonical)
    {
        ArgumentException.ThrowIfNullOrEmpty(emittedCanonical);
        var covered = new HashSet<string>(ExpandRelationsWithFamily(declaredRelationNames), StringComparer.Ordinal);
        var emitted = RelationTypeRegistry.Resolve(emittedCanonical);
        if (covered.Contains(emitted.Canonical)) return true;
        var root = FamilyRootCanonical(emitted.Canonical);
        return covered.Contains(root);
    }

    public static string FamilyRootCanonical(string canonical)
    {
        var cur = RelationTypeRegistry.Resolve(canonical);
        while (cur.ParentId is { } parentId)
        {
            string? parentName;
            unsafe
            {
                Hash128 pid = parentId;
                parentName = Marshal.PtrToStringUTF8(NativeInterop.RelationCanonicalForTypeId(&pid));
            }
            if (string.IsNullOrEmpty(parentName)) break;
            cur = RelationTypeRegistry.Resolve(parentName);
        }
        return cur.Canonical;
    }

    private static void AddWithAncestors(HashSet<string> set, string name)
    {
        var cur = RelationTypeRegistry.Resolve(name);
        set.Add(cur.Canonical);
        while (cur.ParentId is { } parentId)
        {
            string? parentName;
            unsafe
            {
                Hash128 pid = parentId;
                parentName = Marshal.PtrToStringUTF8(NativeInterop.RelationCanonicalForTypeId(&pid));
            }
            if (string.IsNullOrEmpty(parentName)) break;
            if (!set.Add(parentName)) break;
            cur = RelationTypeRegistry.Resolve(parentName);
        }
    }

    public static async Task<BootstrapIntentBuilder> RegisterAsync(
        IDecomposerContext context,
        Hash128 sourceId,
        string sourceName,
        Hash128 trustClassId,
        IEnumerable<string>? typeNodeNames = null,
        IEnumerable<string>? relationNodeNames = null,
        ConcurrentDictionary<string, byte>? readbackNames = null,
        CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(sourceId, sourceName, trustClassId);
        if (typeNodeNames is not null)
            foreach (var n in typeNodeNames) boot.AddType(n);
        if (relationNodeNames is not null)
        {
            foreach (var n in ExpandRelationsWithFamily(relationNodeNames))
                boot.AddRelationType(n);
        }
        await context.Writer.ApplyAsync(boot.Build(), ct);
        if (readbackNames is not null)
            foreach (var n in boot.CanonicalNames)
                readbackNames.TryAdd(n, 0);
        return boot;
    }

    /// <summary>
    /// Sealed-Initialize path: register types/relations from an <see cref="ISourceManifest"/>,
    /// then deposit <see cref="ISourceManifest.License"/> as witnessed credit attestations
    /// on the source entity (HAS_LICENSE / HAS_ATTRIBUTION / HAS_SOURCE_URL / HAS_CITATION /
    /// HAS_VERSION). New relations renumber highway bits — batched into the campaign reseed queue.
    /// </summary>
    public static async Task<BootstrapIntentBuilder> RegisterManifestAsync(
        IDecomposerContext context,
        ISourceManifest manifest,
        ConcurrentDictionary<string, byte>? readbackNames = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var boot = await RegisterAsync(
            context,
            manifest.SourceId,
            manifest.SourceName,
            manifest.TrustClass,
            typeNodeNames: manifest.TypeNodeNames,
            relationNodeNames: CreditRelations.Concat(manifest.Relations),
            readbackNames: readbackNames,
            ct: ct);
        await DepositLicenseAsync(context, manifest, ct);
        return boot;
    }

    private static readonly string[] CreditRelations =
    [
        "HAS_LICENSE", "HAS_ATTRIBUTION", "HAS_SOURCE_URL", "HAS_CITATION", "HAS_VERSION",
    ];

    /// <summary>
    /// Deposit license metadata as witnessed scalar edges on the source entity.
    /// Skips when license is <see cref="SourceLicense.Unknown"/> with no fields set.
    /// </summary>
    public static async Task DepositLicenseAsync(
        IDecomposerContext context,
        ISourceManifest manifest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var license = manifest.License;
        if (ReferenceEquals(license, SourceLicense.Unknown)
            && string.IsNullOrEmpty(license.Spdx)
            && string.IsNullOrEmpty(license.Url)
            && string.IsNullOrEmpty(license.Copyright)
            && string.IsNullOrEmpty(license.Citation)
            && string.IsNullOrEmpty(license.Version))
            return;

        var b = new SubstrateChangeBuilder(
            manifest.SourceId, $"bootstrap/license/{manifest.SourceName}", null,
            entityCapacity: 16, physicalityCapacity: 0, attestationCapacity: 16);
        bool any = false;

        void Attest(string relation, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (ContentEmitter.Emit(b, value, manifest.SourceId) is not { } objId) return;
            b.AddAttestation(NativeAttestation.Categorical(
                manifest.SourceId, relation, objId, manifest.SourceId, null,
                SourceTrust.SubstrateMandate));
            any = true;
        }

        Attest("HAS_LICENSE", license.Spdx ?? license.Name);
        Attest("HAS_ATTRIBUTION", license.Copyright);
        Attest("HAS_SOURCE_URL", license.Url);
        Attest("HAS_CITATION", license.Citation);
        Attest("HAS_VERSION", license.Version);

        if (!any) return;
        await context.Writer.ApplyAsync(b.Build(), ct);
    }
}
