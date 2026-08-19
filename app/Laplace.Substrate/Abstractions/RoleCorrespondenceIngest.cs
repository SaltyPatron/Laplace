using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One mapping between two semantic roles whose labels are meaningful only beneath
/// their owning roleset/class/frame. Parent identity is lifted into both endpoints;
/// it never hides solely in attestation context.
/// </summary>
public readonly record struct RoleCorrespondenceRecord(
    string SubjectParentKey,
    Hash128 SubjectParentTypeId,
    string SubjectRoleKey,
    string ObjectParentKey,
    Hash128 ObjectParentTypeId,
    string ObjectRoleKey,
    double Magnitude = 1.0);

public sealed class RoleCorrespondenceHandler : IIngestRecordHandler<RoleCorrespondenceRecord>
{
    private readonly Hash128 _sourceId;
    private readonly Hash128 _relationTypeId;
    private readonly double _trust;

    public RoleCorrespondenceHandler(Hash128 sourceId, Hash128 relationTypeId, double trust)
    {
        _sourceId = sourceId;
        _relationTypeId = relationTypeId;
        _trust = trust;
    }

    public IIngestDeferredUnit CreateDeferredUnit(RoleCorrespondenceRecord record) =>
        new Unit(record, _sourceId, _relationTypeId, _trust);

    public void WalkWitness(
        RoleCorrespondenceRecord record, Hash128 root,
        SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

    private sealed class Unit(
        RoleCorrespondenceRecord record, Hash128 sourceId,
        Hash128 relationTypeId, double trust) : IIngestDeferredUnit
    {
        public TierTree? TreeForBatchProbe => null;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            Hash128? subjectParent = AnchorAdmission.Id(
                record.SubjectParentKey, record.SubjectParentTypeId);
            Hash128? objectParent = AnchorAdmission.Id(
                record.ObjectParentKey, record.ObjectParentTypeId);
            if (subjectParent is null || objectParent is null) return default;

            RoleIdentityKind subjectKind = RoleAnchor.KindForParentType(record.SubjectParentTypeId);
            RoleIdentityKind objectKind = RoleAnchor.KindForParentType(record.ObjectParentTypeId);
            Hash128? subjectRole = RoleAnchor.Declare(
                builder, subjectKind, subjectParent.Value, record.SubjectRoleKey,
                RoleAnchor.EntityTypeFor(subjectKind), sourceId);
            Hash128? objectRole = RoleAnchor.Declare(
                builder, objectKind, objectParent.Value, record.ObjectRoleKey,
                RoleAnchor.EntityTypeFor(objectKind), sourceId);
            if (subjectRole is null || objectRole is null) return default;

            builder.AddAttestation(NativeAttestation.ResolvedScored(
                subjectRole.Value, relationTypeId, objectRole.Value,
                sourceId, null, trust, record.Magnitude, arenaScale: 1.0));
            return subjectRole.Value;
        }

        public void Dispose() { }
    }
}
