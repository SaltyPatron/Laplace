using System.Text.Json;
using Laplace.Cli;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class FoundryReadoutTests
{
    private static IReadOnlyList<LlamaTokenizerParser.TokenRecord> Tokens()
    {
        if(!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(LaplaceInstall.ResolveT0Perfcache());
        return LlamaTokenizerParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new {
            added_tokens=new[] {new{id=0,special=true},new{id=1,special=true},new{id=2,special=true}},
            model=new {vocab=new Dictionary<string,int> {
                ["<unk>"]=0,["<s>"]=1,["</s>"]=2,["king"]=3,["Ġking"]=4,
                ["King"]=5,["ĠKing"]=6,["dog"]=7,["Ġdog"]=8,["cat"]=9,["Ġcat"]=10
            }}
        }));
    }

    [Fact]
    public void RealizationBindingsPreserveExactContentIdsAndCase()
    {
        var tokens=Tokens();
        var before=tokens.Select(t=>t.EntityId).ToArray();
        var binding=FoundryReadout.Bind(tokens,11);
        Assert.NotEqual(tokens[3].EntityId,tokens[4].EntityId);
        Assert.NotEqual(tokens[3].EntityId,tokens[5].EntityId);
        Assert.Equal(3,binding.InputOperand[4]);
        Assert.Equal(5,binding.InputOperand[6]);
        Assert.Equal(4,binding.OperandOutput[3]);
        Assert.Equal(before,tokens.Select(t=>t.EntityId));
    }

    [Fact]
    public void NativeCompilerSumsDuplicateEdgesAndCancelsRmsNorm()
    {
        var binding=FoundryReadout.Bind(Tokens(),11);
        var plane=new FoundryExport.PlaneCoo([3,3,3],[7,7,9],[2,3,1]);
        var program=new FoundryReadout.Program(binding,plane,[],null!);
        const int d=16; const double eps=1e-5;
        program.Compile(11,d,eps,out var embed,out var head);
        double Score(int input,int output) {
            double sum=0,dot=0;
            for(int c=0;c<d;c++) {sum+=embed[input*d+c]*embed[input*d+c];dot+=embed[input*d+c]*head[output*d+c];}
            return dot/Math.Sqrt(sum/d+eps);
        }
        Assert.Equal(Math.Log(5.0/6*11),Score(4,8),11);
        Assert.Equal(Math.Log(1.0/6*11),Score(4,10),11);
        Assert.Equal(Score(3,8),Score(4,8),12);
        Assert.True(Score(4,8)>Score(4,0));
        // The distinct uppercase operand has no supplied transition and ends.
        Assert.Equal(1.0,Score(6,2),12);
        Assert.All(head,x=>Assert.True(double.IsFinite(x)));
    }

    [Fact]
    public void NegativeTransferWeightsAreRejectedBeforeSerialization()
    {
        var program=new FoundryReadout.Program(FoundryReadout.Bind(Tokens(),11),
            new([3],[7],[-1]),[],null!);
        Assert.Throws<InvalidOperationException>(()=>program.Compile(11,16,1e-5,out _,out _));
    }

    [Fact]
    public void PathRecipeKeepsDirectionAndOptionalStepsInItsKey()
    {
        using var json=JsonDocument.Parse("""{"op":"path","steps":[{"types":["HAS_SENSE"]},{"types":["IS_SENSE_OF"],"reverse":true,"optional":true}]}""");
        var op=OperatorSpec.FromJson(json.RootElement);
        Assert.Equal(2,op.Steps!.Count);
        Assert.True(op.Steps[1].Reverse);
        Assert.True(op.Steps[1].Optional);
        Assert.Contains("HAS_SENSE",op.Key);
    }
    [Fact]
    public void ReadoutSetPolicyRemainsSeparateFromThePathIdentity()
    {
        using var json=JsonDocument.Parse("""{"op":"path","steps":[{"types":["IS_A"]}],"scoring":"set","max_outside_probability":0.000001}""");
        var op=OperatorSpec.FromJson(json.RootElement);
        Assert.True(op.EqualCandidates);Assert.Equal(1e-6,op.MaxOutsideProbability);
        using var plain=JsonDocument.Parse("""{"op":"path","steps":[{"types":["IS_A"]}]}""");
        Assert.Equal(OperatorSpec.FromJson(plain.RootElement).Key,op.Key);
    }

    [Fact]
    public void NonorthogonalBasisUsesDifferentScoreAndValueEquations()
    {
        double[] r=[2,1,0,3];
        var plane=new FoundryExport.PlaneCoo([0,1],[1,0],[2,5]);
        var result=FoundryExport.LowerOperator(r,2,2,plane);
        Assert.Equal(2,result.BasisRank);
        for(int x=0;x<2;x++) for(int y=0;y<2;y++) {
            double score=0,value=0,expectedValue=0;
            for(int i=0;i<2;i++) for(int j=0;j<2;j++) score+=r[x*2+i]*result.Scores[i*2+j]*r[y*2+j];
            for(int i=0;i<2;i++) value+=r[x*2+i]*result.Values[i*2+y];
            for(int e=0;e<plane.Nnz;e++) if(plane.Rows[e]==x) expectedValue+=plane.Vals[e]*r[plane.Cols[e]*2+y];
            Assert.Equal(x==y?0:x==0?2:5,score,10);
            Assert.Equal(expectedValue,value,10);
        }
        double[] applied=new double[4];
        FoundryExport.ApplyOperator(r,2,2,result.Values,1,applied);
        Assert.Equal(0,applied[0],10);Assert.Equal(6,applied[1],10);
        Assert.Equal(10,applied[2],10);Assert.Equal(5,applied[3],10);
        Assert.True(result.ScoreError<1e-7);Assert.True(result.ValueError<1e-7);
    }

    [Fact]
    public void RankDeficientBasisReportsUnrepresentableOperatorMass()
    {
        var result=FoundryExport.LowerOperator([1,0,1,0],2,2,new([0],[1],[1]));
        Assert.Equal(1,result.BasisRank);
        Assert.Equal(Math.Sqrt(0.75),result.ScoreError,10);
        Assert.Equal(Math.Sqrt(0.5),result.ValueError,10);
    }

    [Fact]
    public void RepeatedHeadsReceiveDisjointModesAndEmptyTail()
    {
        var op=new OperatorSpec("unary",null,null);
        var f=new FoundryExport.Factors([1,2,3,4,5,6],[7,8,9,10,11,12],3,2,0,1);
        var first=FoundryExport.HeadFactors(f,[op,op,op,op],0,2);
        var second=FoundryExport.HeadFactors(f,[op,op,op,op],1,2);
        var last=FoundryExport.HeadFactors(f,[op,op,op,op],3,2);
        Assert.Equal(new float[]{1,2},first.Left);
        Assert.Equal(new float[]{3,4},second.Left);
        Assert.Equal(0,last.Rank);Assert.Empty(last.Left);
    }

    [Fact]
    public unsafe void GraphBasisKeepsIsolatesOutOfTheEigensolve()
    {
        int[] rows=[0,1,2],cols=[1,2,0];double[] w=[1,1,1],basis=new double[4];nuint rank=0;
        int rc;fixed(int* r=rows,c=cols)fixed(double* p=w,b=basis)
            rc=Laplace.Engine.Synthesis.NativeInterop.GraphBasis(r,c,p,3,4,1,b,&rank);
        Assert.Equal(0,rc);Assert.Equal((nuint)1,rank);Assert.Equal(0,basis[3]);
        Assert.True(basis.Take(3).Sum(x=>x*x)>0);Assert.All(basis,x=>Assert.True(double.IsFinite(x)));
    }

    [Fact]
    public void FullFactorsPreserveTheOperatorScale()
    {
        double[] m=[4,1,2,3];var f=FoundryExport.Factor(m,2,2,0,false,preserveSpectrum:true);
        for(int x=0;x<2;x++)for(int y=0;y<2;y++) {
            double value=0;for(int k=0;k<f.Rank;k++)value+=(double)f.Left[k*2+x]*f.Right[k*2+y];
            Assert.True(Math.Abs(m[x*2+y]-value)<1e-5);
        }
    }

    [Fact]
    public unsafe void SelectedOperatorEmbeddingCarriesTheTargetWithConstantRms()
    {
        var tokens=Tokens();var program=new FoundryReadout.Program(FoundryReadout.Bind(tokens,11),new([3,3],[7,9],[5,1]),[],null!);
        var e=program.Embed(11,16,tokens,out int features);
        Assert.InRange(features,2,10);
        for(int x=0;x<11;x++)Assert.Equal(16,e.Skip(x*16).Take(16).Sum(v=>v*v),10);
        Assert.Equal(e.Skip(3*16).Take(16),e.Skip(4*16).Take(16));
        var states=e.Select(x=>(float)x).ToArray();var head=new float[11*16];
        nuint rank=0;double error=0,accuracy=0;int rc;
        fixed(float* r=states,h=head)fixed(int* input=program.Bindings.InputOperand,emit=program.Bindings.OperandOutput,rows=program.Plane.Rows,cols=program.Plane.Cols)
        fixed(double* w=program.Plane.Vals)
            rc=Laplace.Engine.Synthesis.NativeInterop.ReadoutFit(r,11,16,input,emit,rows,cols,w,(nuint)program.Plane.Nnz,h,&rank,&error,&accuracy);
        Assert.Equal(0,rc);Assert.True(error<1e-5);Assert.Equal(1,accuracy);
    }
}
