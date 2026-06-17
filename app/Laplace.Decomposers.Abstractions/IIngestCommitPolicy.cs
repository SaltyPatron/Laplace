namespace Laplace.Decomposers.Abstractions;




public enum IngestCommitParallelism
{
    
    
    
    
    StrictSerial,

    
    
    
    
    EpochBarrier,

    
    
    
    Unordered,
}





public interface IIngestCommitPolicy
{
    IngestCommitParallelism CommitParallelism { get; }
}
