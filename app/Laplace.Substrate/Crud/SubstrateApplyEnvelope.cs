namespace Laplace.SubstrateCRUD;

/// <summary>
/// Keeps source input alive from decomposition through the transaction that
/// admits the resulting change. Each queued change owns one retained lease;
/// the runner releases it only after the change has succeeded or permanently
/// left the apply pipeline.
/// </summary>
public sealed class SubstrateApplyEnvelope : IDisposable
{
    private sealed class SharedState
    {
        private readonly Func<CancellationToken, ValueTask> _precommitVerifier;
        private IDisposable? _lifetime;
        private int _references = 1;

        internal SharedState(
            Func<CancellationToken, ValueTask> precommitVerifier,
            IDisposable lifetime)
        {
            _precommitVerifier = precommitVerifier;
            _lifetime = lifetime;
        }

        internal void Retain()
        {
            while (true)
            {
                int references = Volatile.Read(ref _references);
                if (references <= 0)
                    throw new ObjectDisposedException(nameof(SubstrateApplyEnvelope));
                if (Interlocked.CompareExchange(
                        ref _references, references + 1, references) == references)
                    return;
            }
        }

        internal ValueTask VerifyAsync(CancellationToken ct) => _precommitVerifier(ct);

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            Interlocked.Exchange(ref _lifetime, null)?.Dispose();
        }
    }

    private SharedState? _state;

    private SubstrateApplyEnvelope(SharedState state) => _state = state;

    public static SubstrateApplyEnvelope Own(
        IDisposable lifetime,
        Func<CancellationToken, ValueTask> precommitVerifier)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(precommitVerifier);
        return new SubstrateApplyEnvelope(new SharedState(precommitVerifier, lifetime));
    }

    /// <summary>Creates the lease transferred to one emitted change.</summary>
    public SubstrateApplyEnvelope Retain()
    {
        SharedState state = _state
            ?? throw new ObjectDisposedException(nameof(SubstrateApplyEnvelope));
        state.Retain();
        return new SubstrateApplyEnvelope(state);
    }

    internal static Func<CancellationToken, ValueTask>? ComposeVerifier(
        IReadOnlyList<SubstrateChange> changes)
    {
        List<SharedState>? states = null;
        HashSet<SharedState>? seen = null;
        for (int i = 0; i < changes.Count; i++)
        {
            SharedState? state = changes[i].ApplyEnvelope?._state;
            if (state is null) continue;
            seen ??= new HashSet<SharedState>(ReferenceEqualityComparer.Instance);
            if (seen.Add(state)) (states ??= []).Add(state);
        }
        if (states is null) return null;

        return async ct =>
        {
            foreach (SharedState state in states)
            {
                ct.ThrowIfCancellationRequested();
                await state.VerifyAsync(ct).ConfigureAwait(false);
            }
        };
    }

    internal static void Release(IReadOnlyList<SubstrateChange> changes)
    {
        for (int i = 0; i < changes.Count; i++)
            changes[i].ApplyEnvelope?.Dispose();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _state, null)?.Release();
    }
}
