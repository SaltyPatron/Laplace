using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Native=Laplace.Engine.Synthesis.NativeInterop;
namespace Laplace.Decomposers.Model;

/// <summary>Bulk source/codec orchestration. Tensor construction and layer execution
/// stay inside liblaplace_synthesis; no managed inference or matrix mathematics.</summary>
public sealed unsafe class NativeSynthesisProgram : IDisposable
{
    private IntPtr _program;
    private readonly GCHandle _self;
    private IntPtr _gguf,_safe;
    private Exception? _failure;
    private bool _disposed;
    public List<object> Tensors {get;}=[];
    public List<object> Calculations {get;}=[];
    public NativeSynthesisProgram(int vocab,int width,double[] embedding,int layers,bool rope,double theta,
        double attentionGain,double residualGain,int bos=1,int eos=2,double epsilon=1e-5)
    {
        fixed(double* e=embedding)_program=Native.ProgramCreate((nuint)vocab,(nuint)width,e,bos,eos,epsilon,theta,rope?1:1e-30,attentionGain,residualGain,(nuint)layers);
        if(_program==IntPtr.Zero)throw new InvalidOperationException("native synthesis program rejected the architecture or input representation");
        _self=GCHandle.Alloc(this);
    }
    private void Check(int rc){if(rc<0)throw new InvalidOperationException(Marshal.PtrToStringUTF8(Native.ProgramError(_program))??"native synthesis failed");}
    public void TieEmbeddings(bool tied)=>Check(Native.ProgramTieEmbeddings(_program,tied?1:0));
    public void OutputFloor(double bound,bool equalCandidates)=>Check(Native.ProgramOutputFloor(_program,bound,equalCandidates?1:0));
    public void ObservationReadout(bool enabled)=>Check(Native.ProgramObservationReadout(_program,enabled?1:0));
    public int AddPlane(int[] rows,int[] columns,double[] weights)
    {
        int rc;fixed(int* r=rows,c=columns)fixed(double* w=weights)rc=Native.ProgramPlane(_program,r,c,w,(nuint)rows.Length);
        Check(rc);return rc;
    }
    public void AddLayer(int heads,int kv,int intermediate,int[] operators,int ffn,int[] experts,int used)
    {fixed(int* h=operators,e=experts)Check(Native.ProgramLayer(_program,(nuint)heads,(nuint)kv,(nuint)intermediate,h,ffn,e,(nuint)experts.Length,(nuint)used));}
    public void AddSequence(int[] tokens,double weight=1)
    {fixed(int* t=tokens)Check(Native.ProgramSequence(_program,t,(nuint)tokens.Length,weight));}
    public void Compile(int readout,int[] input,int[] output,IntPtr gguf,IntPtr safetensors)
    {
        _gguf=gguf;_safe=safetensors;
        fixed(int* i=input,o=output)Check(Native.ProgramCompile(_program,readout,i,o,&Emit,&Trace,GCHandle.ToIntPtr(_self)));
        if(_failure is not null)throw new InvalidOperationException("native synthesis sink failed",_failure);
    }
    public void WriteStates(string file)
    {
        nuint rows=0,width=0;float* states=Native.ProgramStates(_program,&rows,&width);
        using var stream=File.Create(file);using var writer=new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("LPSTATE1"));writer.Write((long)rows);writer.Write((long)width);
        long bytes=checked((long)rows*(long)width*sizeof(float));
        using var index=File.Create(file+".index.bin");using var iw=new BinaryWriter(index);iw.Write(System.Text.Encoding.ASCII.GetBytes("LPINDEX1"));iw.Write((long)rows);
        int* tokens=null;int* parents=null;int* positions=null;Native.ProgramNodes(_program,&tokens,&parents,&positions);
        for(nuint x=0;x<rows;x++){iw.Write(tokens[x]);iw.Write(parents[x]);iw.Write(positions[x]);}
        for(long offset=0;offset<bytes;){int count=(int)Math.Min(1024*1024,bytes-offset);stream.Write(new ReadOnlySpan<byte>((byte*)states+offset,count));offset+=count;}
    }
    public void WriteTargets(string file,int vocab)
    {
        nuint* nodes=null;double* floors=null;nuint* offsets=null;int* columns=null;double* deltas=null;
        nuint rows=Native.ProgramTargets(_program,&nodes,&floors,&offsets,&columns,&deltas);
        using var stream=File.Create(file);using var writer=new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("LPTARG01"));writer.Write((long)vocab);writer.Write((long)rows);
        for(nuint x=0;x<rows;x++){
            writer.Write((long)nodes[x]);writer.Write(floors[x]);writer.Write((long)(offsets[x+1]-offsets[x]));
            for(nuint y=offsets[x];y<offsets[x+1];y++){writer.Write(columns[y]);writer.Write(deltas[y]);}
        }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static int Emit(IntPtr state,byte* hfName,byte* ggName,nuint* shape,nuint rank,float* data,nuint count)
    {
        var self=(NativeSynthesisProgram)GCHandle.FromIntPtr(state).Target!;
        try {
            string? hf=Marshal.PtrToStringUTF8((IntPtr)hfName),gg=Marshal.PtrToStringUTF8((IntPtr)ggName);
            nuint bytes=checked(count*sizeof(float));
            if(gg is not null&&self._gguf!=IntPtr.Zero){nuint* dims=stackalloc nuint[(int)rank];for(int x=0;x<(int)rank;x++)dims[x]=shape[(int)rank-1-x];
                if(Native.GgufWriterAddTensor(self._gguf,gg,0,dims,rank,data)!=0)throw new IOException($"GGUF rejected {gg}");}
            if(hf is not null&&self._safe!=IntPtr.Zero)
                if(Native.FormatWriterAddTensor(self._safe,hf,0,shape,rank,data,(nuint)bytes)!=0)throw new IOException($"SafeTensors rejected {hf}");
            self.Tensors.Add(new{hf_name=hf,gguf_name=gg,shape=new ReadOnlySpan<nuint>(shape,(int)rank).ToArray().Select(x=>(ulong)x).ToArray(),
                bytes=(ulong)bytes,sha256=Hash(data,bytes)});
            return 0;
        }catch(Exception e){self._failure=e;return -1;}
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void Trace(IntPtr state,nuint layer,byte* phase,nuint rank,double error,double secondary)
    {
        var self=(NativeSynthesisProgram)GCHandle.FromIntPtr(state).Target!;try {string name=Marshal.PtrToStringUTF8((IntPtr)phase)??"";
        self.Calculations.Add(new{layer=(ulong)layer,phase=name,rank=(ulong)rank,error,secondary});
        Console.WriteLine($"  native layer {layer} {name}: rank/rows {rank}, residual {error:G6}, secondary {secondary:G6}");
        }catch(Exception e){self._failure=e;}
    }
    private static string Hash(float* data,nuint bytes)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(nuint offset=0;offset<bytes;){int chunk=(int)Math.Min((ulong)(bytes-offset),1024*1024);hash.AppendData(new ReadOnlySpan<byte>((byte*)data+offset,chunk));offset+=(nuint)chunk;}
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public void Dispose(){if(_disposed)return;_disposed=true;if(_program!=IntPtr.Zero){Native.ProgramFree(_program);_program=IntPtr.Zero;}_self.Free();}
}
