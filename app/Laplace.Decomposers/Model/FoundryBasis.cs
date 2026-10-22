using Native=Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

internal static class FoundryBasis
{
    internal static double[] Coordinates(IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,int vocab)
    {
        var coordinates=new double[checked(vocab*4)];
        foreach(var t in tokens) {
            coordinates[t.TokenId*4]=t.ContentX;coordinates[t.TokenId*4+1]=t.ContentY;
            coordinates[t.TokenId*4+2]=t.ContentZ;coordinates[t.TokenId*4+3]=t.ContentM;
        }
        return coordinates;
    }

    internal static unsafe object Refine(double[] embedding,int vocab,int width,int rank,double[] coordinates,int[] input)
    {
        Native.BasisReceipt receipt=default;int rc;
        fixed(double* e=embedding,c=coordinates)fixed(int* i=input)
            rc=Native.BasisRefine(e,(nuint)vocab,(nuint)width,(nuint)rank,c,i,&receipt);
        if(rc!=0)throw new InvalidOperationException($"native basis QR/alignment failed ({rc}); rows {vocab}, width {width}, rank {rank}");
        return new {conditioning="rank-revealing SVD; native gram_schmidt_orthonormalize (Householder QR)",
            alignment="full-dimensional orthogonal Procrustes against immutable 4D anchors; complement retained",
            rank=(ulong)receipt.Rank,anchors=(ulong)receipt.Anchors,
            qr_orthogonality_error=receipt.OrthogonalityError,column_span_relative_error=receipt.SpanRelativeError,
            frame_orthogonality_error=receipt.FrameOrthogonalityError,
            anchor_error_before=receipt.AnchorErrorBefore,anchor_error_after=receipt.AnchorErrorAfter,
            alignment_applied=receipt.Anchors>0,
            weight_policy="recompile score, value, gate and readout equations in the resulting representation"};
    }

    internal static unsafe double[] Spectral(int vocab,int width,IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        FoundryExport.PlaneCoo graph,out int features,out object receipt)
    {
        var input=FoundryReadout.Bind(tokens,vocab).InputOperand;var coordinates=Coordinates(tokens,vocab);
        var embedding=new double[checked((long)vocab*width)];nuint used=0;int rc;Native.SpectralReceipt spectral=default;
        fixed(int* i=input,r=graph.Rows,c=graph.Cols)fixed(double* p=coordinates,w=graph.Vals,e=embedding)
            rc=Native.SpectralEmbed((nuint)vocab,(nuint)width,i,p,r,c,w,(nuint)graph.Nnz,e,&used,&spectral);
        if(rc!=0)throw new InvalidOperationException($"native normalized-Laplacian embedding failed ({rc}); rows {vocab}, width {width}");
        features=checked((int)used);receipt=new {
            spectral=new {operation="generalized normalized-Laplacian eigenmaps before QR and full-frame alignment",
                active_vertices=(ulong)spectral.ActiveVertices,components=(ulong)spectral.Components,
                requested_modes=(ulong)spectral.RequestedModes,retained_modes=(ulong)spectral.RetainedModes,
                retained_null_modes=(ulong)(spectral.Components>1?Math.Min(spectral.Components-1,spectral.RetainedModes):0),
                combined_rank=(ulong)spectral.CombinedRank,eigen_residual_max=spectral.EigenResidualMax,
                mass_orthogonality_error=spectral.MassOrthogonalityError,discarded_svd_energy=spectral.DiscardedSvdEnergy},
            frame=Refine(embedding,vocab,width,features-2,coordinates,input)};return embedding;
    }
}
