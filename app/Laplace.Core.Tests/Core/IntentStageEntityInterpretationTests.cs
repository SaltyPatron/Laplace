using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class IntentStageEntityInterpretationTests
{
    private static byte[] Tuples(IntentStage stage, IntentStageTable table)
    {
        var (pointer,length) = stage.TupleBuffer(table);
        var bytes = new byte[checked((int)length)];
        if (bytes.Length > 0) Marshal.Copy(pointer,bytes,0,bytes.Length);
        return bytes;
    }

    [Fact]
    public void CompleteFacetTransportPreservesHistoricalRowsDigestAndPartitions()
    {
        var id = Hash128.OfCanonical("facet/transport/entity");
        var type = Hash128.OfCanonical("facet/transport/type");
        var source = Hash128.OfCanonical("facet/transport/source");
        using var stage = IntentStage.New(1);
        stage.AddEntity(id,3,type,source);
        var entities = Tuples(stage,IntentStageTable.Entities);
        var digest = stage.SemanticDigest();
        stage.AddEntityInterpretation(id,7,Hash128.OfCanonical("facet/transport/other"),null);
        Assert.True(stage.EntityInterpretationsComplete);
        Assert.Equal(2,stage.EntityInterpretationCount);
        Assert.Equal(1,stage.EntityCount);
        Assert.Equal(entities,Tuples(stage,IntentStageTable.Entities));
        Assert.Equal(digest,stage.SemanticDigest());
        using var clone = IntentStage.FromTupleBytes(entities,[],[],1_048_576);
        Assert.False(clone.EntityInterpretationsComplete);
        clone.ImportEntityInterpretations(stage.EmitEntityInterpretationTuples());
        Assert.True(clone.EntityInterpretationsComplete);
        Assert.Equal(stage.EmitEntityInterpretationTuples(),clone.EmitEntityInterpretationTuples());
        Assert.Equal(digest,clone.SemanticDigest());
        var parts = clone.Partition(3);
        try
        {
            Assert.All(parts,part => Assert.True(part.EntityInterpretationsComplete));
            Assert.Equal(2,parts.Sum(part => part.EntityInterpretationCount));
            Assert.Equal(1,parts.Sum(part => part.EntityCount));
            var nonempty = Assert.Single(parts.Where(part => part.EntityInterpretationCount > 0));
            Assert.Equal(1,nonempty.EntityCount);
            Assert.Equal(stage.EmitEntityInterpretationTuples(),nonempty.EmitEntityInterpretationTuples());
        }
        finally { foreach (var part in parts) part.Dispose(); }
    }

    [Fact]
    public void LegacyAppendSeedsOldRowsButExplicitCompleteEmptyDoesNotInventFacets()
    {
        var id = Hash128.OfCanonical("facet/legacy/entity");
        var type = Hash128.OfCanonical("facet/legacy/type");
        using var source = IntentStage.New(1);
        source.AddEntity(id,3,type,null);
        var entities = Tuples(source,IntentStageTable.Entities);
        using var legacy = IntentStage.FromTupleBytes(entities,[],[],1_048_576);
        Assert.False(legacy.EntityInterpretationsComplete);
        legacy.AddEntityInterpretation(id,5,type,null);
        Assert.True(legacy.EntityInterpretationsComplete);
        Assert.Equal(2,legacy.EntityInterpretationCount);
        using var complete = IntentStage.FromTupleBytes(entities,[],[],1_048_576);
        complete.ImportEntityInterpretations([]);
        Assert.True(complete.EntityInterpretationsComplete);
        Assert.Equal(0,complete.EntityInterpretationCount);
        Assert.Equal(1,complete.EntityCount);
        complete.AddEntityInterpretation(id,5,type,null);
        Assert.Equal(1,complete.EntityInterpretationCount);
        Assert.Equal(entities,Tuples(complete,IntentStageTable.Entities));
    }

    [Fact]
    public void MalformedReplacementLeavesFacetAndSemanticStateUnchanged()
    {
        using var stage = IntentStage.New(1);
        stage.AddEntity(Hash128.OfCanonical("facet/invalid/entity"),3,
            Hash128.OfCanonical("facet/invalid/type"),null);
        var before = stage.EmitEntityInterpretationTuples();
        var digest = stage.SemanticDigest();
        Assert.Throws<InvalidOperationException>(() =>
            stage.ImportEntityInterpretations(before.AsSpan(0,before.Length-1)));
        Assert.True(stage.EntityInterpretationsComplete);
        Assert.Equal(1,stage.EntityInterpretationCount);
        Assert.Equal(before,stage.EmitEntityInterpretationTuples());
        Assert.Equal(digest,stage.SemanticDigest());
    }
}
