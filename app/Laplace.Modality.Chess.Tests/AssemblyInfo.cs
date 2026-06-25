// Run tests sequentially. The deep perft facts (startpos d6 ~119M, kiwipete d5 ~194M, pos5 d5 ~90M)
// each allocate enormous numbers of short-lived Board clones; running them concurrently spikes peak
// memory and GC pressure to the point of destabilizing the test host. One at a time is plenty fast
// and deterministic.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
