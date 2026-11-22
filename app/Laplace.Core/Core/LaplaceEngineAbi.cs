using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Native;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineAuthorityScope
{
    public lp_id* witnesses;
    public uint nwitnesses;
    public int scoped;
    public uint capabilities;
    public int disposition;
    public double min_trust;
    public int allow_write;
    public int reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineChatMessage
{
    public byte* role;
    public byte* content;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineTurnRequest
{
    public byte* firmware_path;
    public byte* model_name;
    public byte* prompt;
    public LpEngineChatMessage* messages;
    public uint nmessages;
    public byte* system_prompt;
    public byte* user;
    public byte* session;
    public int read_only;
    public int max_tokens;
    public double temperature;
    public byte** stop_sequences;
    public uint nstop_sequences;
    public long seed;
    public LpEngineAuthorityScope* authority;
    public lp_envelope* envelope;
    public byte* code_path;
    public byte* code_text;
    public nuint code_len;
    public byte* code_lang;
    public byte* verify_cmd;
    public int ooda_rounds;
    public int write_file;
}

[StructLayout(LayoutKind.Sequential)]
public struct LpEngineWitnessShare
{
    public lp_id witness;
    public double trust;
    public double fisher_info;
    public ulong games;
    public int share_bp;
    public int reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineTurnResponse
{
    public int status_code;
    public int ooda_verified;
    public int ooda_unverified;
    public int ooda_rounds_run;
    public lp_id firmware_id;
    public lp_id program_id;
    public lp_id decision_id;
    public lp_id prompt_id;
    public lp_id response_id;
    public lp_id turn_id;
    public lp_id culprit_id;
    public double culprit_contrast;
    public double top_salience;
    public double mean_confidence;
    public lp_work_counters work;
    public byte* text;
    public nuint text_len;
    public byte* patched_code;
    public nuint patched_len;
    public fixed byte disposition[160];
    public fixed byte error[512];
    public LpEngineWitnessShare* shares;
    public uint nshares;
    public int reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineChessRequest
{
    public byte* fen;
    public byte* variant;
    public LpEngineAuthorityScope* authority;
    public int max_depth;
    public int quiesce_depth;
    public ulong max_nodes;
    public ulong max_wall_us;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineChessResponse
{
    public int status_code;
    public int score_cp;
    public int depth_reached;
    public int syzygy_wdl;
    public int from_syzygy;
    public int reserved;
    public ulong nodes_searched;
    public ulong wall_us;
    public ulong db_probes;
    public lp_id position_id;
    public lp_id move_id;
    public lp_id next_position_id;
    public lp_id claim_id;
    public fixed byte best_uci[8];
    public fixed byte best_san[16];
    public fixed byte source[24];
    public fixed byte error[256];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineMoldRequest
{
    public byte* out_path;
    public byte* model_name;
    public LpEngineAuthorityScope* authority;
    public uint max_vocab;
    public uint hidden_dim;
    public uint n_layers;
    public uint n_heads;
    public int f16_weights;
    public int reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LpEngineMoldResponse
{
    public int status_code;
    public uint n_vocab;
    public uint n_edges;
    public uint hidden_dim;
    public ulong raw_weight_bytes_retained;
    public fixed byte out_path[512];
    public fixed byte error[256];
}

public static unsafe class LaplaceEngineAbi
{
    public const string LibraryName = "laplace_engine_abi";

    private static readonly Lazy<nint> Handle = new(LoadLibrary, LazyThreadSafetyMode.ExecutionAndPublication);

    private static delegate* unmanaged[Cdecl]<byte*, byte*, nuint, nint> _init;
    private static delegate* unmanaged[Cdecl]<nint, LpEngineTurnRequest*, LpEngineTurnResponse*, int> _turn;
    private static delegate* unmanaged[Cdecl]<LpEngineTurnResponse*, void> _turnResponseFree;
    private static delegate* unmanaged[Cdecl]<nint, LpEngineChessRequest*, LpEngineChessResponse*, int> _chessPlay;
    private static delegate* unmanaged[Cdecl]<nint, LpEngineMoldRequest*, LpEngineMoldResponse*, int> _moldCheckpoint;
    private static delegate* unmanaged[Cdecl]<nint, void> _free;

    private static nint EnsureLoaded() => Handle.Value;

    private static nint LoadLibrary()
    {
        EnsureNativeSearchEnvironment();
        var fileName = OperatingSystem.IsWindows() ? "laplace_engine_abi.dll" : "liblaplace_engine_abi.so";
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
            candidates.Add(Path.Combine(baseDir, fileName));

        var envPkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(envPkg))
            candidates.Add(Path.Combine(envPkg, OperatingSystem.IsWindows() ? "bin" : "lib", fileName));

        var envBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BUILD");
        if (!string.IsNullOrWhiteSpace(envBuild))
            candidates.Add(Path.Combine(envBuild, fileName));

        if (LaplaceInstall.TryFindRepoRoot(out var repoRoot))
        {
            var lockFile = Path.GetFullPath(Path.Combine(repoRoot, "..", "Laplace-Operations", "components.lock.tsv"));
            var packagesRoot = Path.Combine(
                LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD") ?? (OperatingSystem.IsWindows() ? @"D:\Libraries\build\Laplace" : Path.GetFullPath(Path.Combine(repoRoot, "..", "build"))),
                "packages", "laplace-engine");
            if (File.Exists(lockFile) && Directory.Exists(packagesRoot))
            {
                var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(lockFile), @"(?m)^engine\tLaplace-Engine\t([0-9a-f]+)");
                if (match.Success)
                {
                    foreach (var dir in Directory.GetDirectories(packagesRoot, match.Groups[1].Value + "-*"))
                        candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "bin" : "lib", fileName));
                }
            }
        }

        nint handle = IntPtr.Zero;
        if (NativeLibrary.TryLoad(LibraryName, typeof(LaplaceEngineAbi).Assembly, null, out var direct))
        {
            handle = direct;
        }
        else
        {
            foreach (var path in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (NativeLibrary.TryLoad(path, out handle))
                    break;
            }
        }

        if (handle == IntPtr.Zero)
            throw new DllNotFoundException($"Unable to load {fileName}. Checked: {string.Join("; ", candidates)}");

        _init = (delegate* unmanaged[Cdecl]<byte*, byte*, nuint, nint>)NativeLibrary.GetExport(handle, "lp_engine_init");
        _turn = (delegate* unmanaged[Cdecl]<nint, LpEngineTurnRequest*, LpEngineTurnResponse*, int>)NativeLibrary.GetExport(handle, "lp_engine_turn");
        _turnResponseFree = (delegate* unmanaged[Cdecl]<LpEngineTurnResponse*, void>)NativeLibrary.GetExport(handle, "lp_engine_turn_response_free");
        _chessPlay = (delegate* unmanaged[Cdecl]<nint, LpEngineChessRequest*, LpEngineChessResponse*, int>)NativeLibrary.GetExport(handle, "lp_engine_chess_play");
        _moldCheckpoint = (delegate* unmanaged[Cdecl]<nint, LpEngineMoldRequest*, LpEngineMoldResponse*, int>)NativeLibrary.GetExport(handle, "lp_engine_mold_checkpoint");
        _free = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(handle, "lp_engine_free");
        return handle;
    }

    private static void EnsureNativeSearchEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;
        var extra = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            extra.Add(AppContext.BaseDirectory);
        foreach (var dir in new[]
        {
            @"C:\Program Files (x86)\Intel\oneAPI\mkl\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\tbb\latest\bin",
            @"C:\Program Files (x86)\Intel\oneAPI\compiler\latest\bin",
        })
        {
            if (Directory.Exists(dir)) extra.Add(dir);
        }
        var pgDir = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_PG_DIR");
        if (!string.IsNullOrWhiteSpace(pgDir))
        {
            var pgBin = Path.Combine(pgDir, "bin");
            if (Directory.Exists(pgBin)) extra.Add(pgBin);
        }
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var missing = extra.Where(d => current.IndexOf(d, StringComparison.OrdinalIgnoreCase) < 0).ToArray();
        if (missing.Length > 0)
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, missing) + Path.PathSeparator + current);
    }

    public static nint lp_engine_init(byte* conninfo, byte* err, nuint err_cap)
    {
        EnsureLoaded();
        return _init(conninfo, err, err_cap);
    }

    public static int lp_engine_turn(nint h, LpEngineTurnRequest* req, LpEngineTurnResponse* @out)
    {
        EnsureLoaded();
        return _turn(h, req, @out);
    }

    public static void lp_engine_turn_response_free(LpEngineTurnResponse* @out)
    {
        EnsureLoaded();
        _turnResponseFree(@out);
    }

    public static int lp_engine_chess_play(nint h, LpEngineChessRequest* req, LpEngineChessResponse* @out)
    {
        EnsureLoaded();
        return _chessPlay(h, req, @out);
    }

    public static int lp_engine_mold_checkpoint(nint h, LpEngineMoldRequest* req, LpEngineMoldResponse* @out)
    {
        EnsureLoaded();
        return _moldCheckpoint(h, req, @out);
    }

    public static void lp_engine_free(nint h)
    {
        EnsureLoaded();
        _free(h);
    }

    internal static string ReadFixedUtf8(byte* buf, int cap)
    {
        if (buf == null || cap <= 0) return string.Empty;
        int len = 0;
        while (len < cap && buf[len] != 0) len++;
        return Encoding.UTF8.GetString(buf, len);
    }
}

public sealed record EngineAuthorityScope(
    IReadOnlyList<Hash128>? Witnesses = null,
    bool Scoped = false,
    uint Capabilities = 0,
    int Disposition = 0,
    double MinTrust = 0.0,
    bool AllowWrite = false);

public sealed record EngineChatMessage(string Role, string Content);

public sealed record EngineTurnRequest(
    string? FirmwarePath = null,
    string? ModelName = null,
    string? Prompt = null,
    IReadOnlyList<EngineChatMessage>? Messages = null,
    string? SystemPrompt = null,
    string? User = null,
    string? Session = null,
    bool ReadOnly = false,
    int MaxTokens = 0,
    double Temperature = 0.0,
    IReadOnlyList<string>? StopSequences = null,
    long Seed = 0,
    EngineAuthorityScope? Authority = null,
    lp_envelope? Envelope = null,
    string? CodePath = null,
    string? CodeText = null,
    string? CodeLang = null,
    string? VerifyCommand = null,
    int OodaRounds = 0,
    bool WriteFile = false);

public sealed record EngineWitnessShare(
    Hash128 Witness,
    double Trust,
    double FisherInfo,
    ulong Games,
    int ShareBasisPoints);

public sealed record EngineTurnResponse(
    int StatusCode,
    bool OodaVerified,
    bool OodaUnverified,
    int OodaRoundsRun,
    Hash128 FirmwareId,
    Hash128 ProgramId,
    Hash128 DecisionId,
    Hash128 PromptId,
    Hash128 ResponseId,
    Hash128 TurnId,
    Hash128 CulpritId,
    double CulpritContrast,
    double TopSalience,
    double MeanConfidence,
    lp_work_counters Work,
    string Text,
    string? PatchedCode,
    string Disposition,
    string Error,
    IReadOnlyList<EngineWitnessShare> Shares);

public sealed record EngineChessRequest(
    string Fen,
    string? Variant = null,
    EngineAuthorityScope? Authority = null,
    int MaxDepth = 0,
    int QuiesceDepth = 0,
    ulong MaxNodes = 0,
    ulong MaxWallUs = 0);

public sealed record EngineChessResponse(
    int StatusCode,
    int ScoreCp,
    int DepthReached,
    int SyzygyWdl,
    bool FromSyzygy,
    ulong NodesSearched,
    ulong WallUs,
    ulong DbProbes,
    Hash128 PositionId,
    Hash128 MoveId,
    Hash128 NextPositionId,
    Hash128 ClaimId,
    string BestUci,
    string BestSan,
    string Source,
    string Error);

public sealed record EngineMoldRequest(
    string OutPath,
    string? ModelName = null,
    EngineAuthorityScope? Authority = null,
    uint MaxVocab = 1024,
    uint HiddenDim = 64,
    uint NLayers = 4,
    uint NHeads = 4,
    bool F16Weights = false);

public sealed record EngineMoldResponse(
    int StatusCode,
    uint NVocab,
    uint NEdges,
    uint HiddenDim,
    ulong RawWeightBytesRetained,
    string OutPath,
    string Error);

public interface ILaplaceEngineRuntime : IDisposable
{
    EngineTurnResponse Turn(EngineTurnRequest request);
    Task<EngineTurnResponse> TurnAsync(EngineTurnRequest request, CancellationToken ct = default);
    EngineChessResponse ChessPlay(EngineChessRequest request);
    EngineMoldResponse MoldCheckpoint(EngineMoldRequest request);
}

/// <summary>
/// Thread-safe managed lifetime wrapper over <c>LpEngineHandle*</c> (<c>laplace_engine_abi</c>).
/// Native Engine calls serialize safely across concurrent managed callers and never retain
/// unmanaged allocations across request boundaries.
/// </summary>
public sealed unsafe class LaplaceEngineRuntime : ILaplaceEngineRuntime
{
    private readonly ReaderWriterLockSlim _disposeLock = new();
    private nint _handle;
    private bool _disposed;

    public LaplaceEngineRuntime(string? conninfo = null)
    {
        var effectiveConn = string.IsNullOrWhiteSpace(conninfo)
            ? ResolveLibpqConnInfo()
            : conninfo.Trim();

        byte[] connBytes = Encoding.UTF8.GetBytes(effectiveConn + '\0');
        Span<byte> errBuf = stackalloc byte[512];
        errBuf.Clear();

        fixed (byte* pConn = connBytes)
        fixed (byte* pErr = errBuf)
        {
            _handle = LaplaceEngineAbi.lp_engine_init(pConn, pErr, (nuint)errBuf.Length);
            if (_handle == nint.Zero)
            {
                string msg = LaplaceEngineAbi.ReadFixedUtf8(pErr, errBuf.Length);
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(msg) ? "lp_engine_init failed." : $"lp_engine_init failed: {msg}");
            }
        }
    }

    public static string ResolveLibpqConnInfo()
    {
        var env = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_CONNINFO");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var npgsql = LaplaceInstall.PostgresConnectionString();
        var csb = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = npgsql };
        var parts = new List<string>();
        void AddIf(string npgsqlKey, string libpqKey)
        {
            if (csb.TryGetValue(npgsqlKey, out var v) && v is not null && !string.IsNullOrWhiteSpace(v.ToString()))
                parts.Add($"{libpqKey}={v}");
        }
        AddIf("Host", "host");
        AddIf("Port", "port");
        AddIf("Database", "dbname");
        AddIf("Username", "user");
        AddIf("Password", "password");
        AddIf("SSL Mode", "sslmode");
        AddIf("Root Certificate", "sslrootcert");
        return string.Join(' ', parts);
    }

    public EngineTurnResponse Turn(EngineTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _disposeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _handle == nint.Zero, this);
            var allocs = new List<nint>();
            try
            {
                byte* AllocUtf8(string? s, out nuint len)
                {
                    if (s is null) { len = 0; return null; }
                    len = (nuint)Encoding.UTF8.GetByteCount(s);
                    var p = Marshal.StringToCoTaskMemUTF8(s);
                    allocs.Add(p);
                    return (byte*)p;
                }

                byte* AllocZ(string? s) => AllocUtf8(s, out _);

                LpEngineChatMessage* msgPtr = null;
                uint msgCount = 0;
                if (request.Messages is { Count: > 0 } msgs)
                {
                    msgCount = checked((uint)msgs.Count);
                    var pArr = Marshal.AllocHGlobal(checked(msgs.Count * sizeof(LpEngineChatMessage)));
                    allocs.Add(pArr);
                    msgPtr = (LpEngineChatMessage*)pArr;
                    for (int i = 0; i < msgs.Count; i++)
                    {
                        msgPtr[i].role = AllocZ(msgs[i].Role);
                        msgPtr[i].content = AllocZ(msgs[i].Content);
                    }
                }

                byte** stopPtr = null;
                uint stopCount = 0;
                if (request.StopSequences is { Count: > 0 } stops)
                {
                    stopCount = checked((uint)stops.Count);
                    var pArr = Marshal.AllocHGlobal(checked(stops.Count * sizeof(byte*)));
                    allocs.Add(pArr);
                    stopPtr = (byte**)pArr;
                    for (int i = 0; i < stops.Count; i++)
                        stopPtr[i] = AllocZ(stops[i]);
                }

                LpEngineAuthorityScope nativeAuth = default;
                LpEngineAuthorityScope* authPtr = null;
                if (request.Authority is { } auth)
                {
                    authPtr = &nativeAuth;
                    if (auth.Witnesses is { Count: > 0 } wits)
                    {
                        var pW = Marshal.AllocHGlobal(checked(wits.Count * sizeof(lp_id)));
                        allocs.Add(pW);
                        var witBuf = (lp_id*)pW;
                        for (int i = 0; i < wits.Count; i++)
                            witBuf[i] = wits[i].ToNative();
                        nativeAuth.witnesses = witBuf;
                        nativeAuth.nwitnesses = checked((uint)wits.Count);
                    }
                    nativeAuth.scoped = auth.Scoped ? 1 : 0;
                    nativeAuth.capabilities = auth.Capabilities;
                    nativeAuth.disposition = auth.Disposition;
                    nativeAuth.min_trust = auth.MinTrust;
                    nativeAuth.allow_write = auth.AllowWrite ? 1 : 0;
                }

                lp_envelope nativeEnv = request.Envelope ?? default;
                lp_envelope* envPtr = request.Envelope.HasValue ? &nativeEnv : null;

                byte* codePtr = AllocUtf8(request.CodeText, out nuint codeLen);

                var nativeReq = new LpEngineTurnRequest
                {
                    firmware_path = AllocZ(request.FirmwarePath),
                    model_name = AllocZ(request.ModelName),
                    prompt = AllocZ(request.Prompt),
                    messages = msgPtr,
                    nmessages = msgCount,
                    system_prompt = AllocZ(request.SystemPrompt),
                    user = AllocZ(request.User),
                    session = AllocZ(request.Session),
                    read_only = request.ReadOnly ? 1 : 0,
                    max_tokens = request.MaxTokens,
                    temperature = request.Temperature,
                    stop_sequences = stopPtr,
                    nstop_sequences = stopCount,
                    seed = request.Seed,
                    authority = authPtr,
                    envelope = envPtr,
                    code_path = AllocZ(request.CodePath),
                    code_text = codePtr,
                    code_len = codeLen,
                    code_lang = AllocZ(request.CodeLang),
                    verify_cmd = AllocZ(request.VerifyCommand),
                    ooda_rounds = request.OodaRounds,
                    write_file = request.WriteFile ? 1 : 0,
                };

                LpEngineTurnResponse nativeOut = default;
                try
                {
                    int rc = LaplaceEngineAbi.lp_engine_turn(_handle, &nativeReq, &nativeOut);
                    var shares = new List<EngineWitnessShare>(checked((int)nativeOut.nshares));
                    if (nativeOut.shares != null)
                    {
                        for (uint i = 0; i < nativeOut.nshares; i++)
                        {
                            var s = nativeOut.shares[i];
                            shares.Add(new EngineWitnessShare(
                                Hash128.FromNative(s.witness), s.trust, s.fisher_info, s.games, s.share_bp));
                        }
                    }

                    string text = nativeOut.text != null && nativeOut.text_len > 0
                        ? Encoding.UTF8.GetString(nativeOut.text, checked((int)nativeOut.text_len))
                        : string.Empty;
                    string? patched = nativeOut.patched_code != null
                        ? Encoding.UTF8.GetString(nativeOut.patched_code, checked((int)nativeOut.patched_len))
                        : null;

                    return new EngineTurnResponse(
                        StatusCode: rc != 0 ? rc : nativeOut.status_code,
                        OodaVerified: nativeOut.ooda_verified != 0,
                        OodaUnverified: nativeOut.ooda_unverified != 0,
                        OodaRoundsRun: nativeOut.ooda_rounds_run,
                        FirmwareId: Hash128.FromNative(nativeOut.firmware_id),
                        ProgramId: Hash128.FromNative(nativeOut.program_id),
                        DecisionId: Hash128.FromNative(nativeOut.decision_id),
                        PromptId: Hash128.FromNative(nativeOut.prompt_id),
                        ResponseId: Hash128.FromNative(nativeOut.response_id),
                        TurnId: Hash128.FromNative(nativeOut.turn_id),
                        CulpritId: Hash128.FromNative(nativeOut.culprit_id),
                        CulpritContrast: nativeOut.culprit_contrast,
                        TopSalience: nativeOut.top_salience,
                        MeanConfidence: nativeOut.mean_confidence,
                        Work: nativeOut.work,
                        Text: text,
                        PatchedCode: patched,
                        Disposition: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.disposition, 160),
                        Error: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.error, 512),
                        Shares: shares);
                }
                finally
                {
                    LaplaceEngineAbi.lp_engine_turn_response_free(&nativeOut);
                }
            }
            finally
            {
                foreach (var p in allocs)
                    Marshal.FreeHGlobal(p);
            }
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public Task<EngineTurnResponse> TurnAsync(EngineTurnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => Turn(request), ct);
    }

    public EngineChessResponse ChessPlay(EngineChessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Fen);
        _disposeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _handle == nint.Zero, this);
            var allocs = new List<nint>();
            try
            {
                byte* AllocZ(string? s)
                {
                    if (s is null) return null;
                    var p = Marshal.StringToCoTaskMemUTF8(s);
                    allocs.Add(p);
                    return (byte*)p;
                }

                LpEngineAuthorityScope nativeAuth = default;
                LpEngineAuthorityScope* authPtr = null;
                if (request.Authority is { } auth)
                {
                    authPtr = &nativeAuth;
                    if (auth.Witnesses is { Count: > 0 } wits)
                    {
                        var pW = Marshal.AllocHGlobal(checked(wits.Count * sizeof(lp_id)));
                        allocs.Add(pW);
                        var witBuf = (lp_id*)pW;
                        for (int i = 0; i < wits.Count; i++)
                            witBuf[i] = wits[i].ToNative();
                        nativeAuth.witnesses = witBuf;
                        nativeAuth.nwitnesses = checked((uint)wits.Count);
                    }
                    nativeAuth.scoped = auth.Scoped ? 1 : 0;
                    nativeAuth.capabilities = auth.Capabilities;
                    nativeAuth.disposition = auth.Disposition;
                    nativeAuth.min_trust = auth.MinTrust;
                    nativeAuth.allow_write = auth.AllowWrite ? 1 : 0;
                }

                var nativeReq = new LpEngineChessRequest
                {
                    fen = AllocZ(request.Fen),
                    variant = AllocZ(request.Variant),
                    authority = authPtr,
                    max_depth = request.MaxDepth,
                    quiesce_depth = request.QuiesceDepth,
                    max_nodes = request.MaxNodes,
                    max_wall_us = request.MaxWallUs,
                };

                LpEngineChessResponse nativeOut = default;
                int rc = LaplaceEngineAbi.lp_engine_chess_play(_handle, &nativeReq, &nativeOut);
                return new EngineChessResponse(
                    StatusCode: rc != 0 ? rc : nativeOut.status_code,
                    ScoreCp: nativeOut.score_cp,
                    DepthReached: nativeOut.depth_reached,
                    SyzygyWdl: nativeOut.syzygy_wdl,
                    FromSyzygy: nativeOut.from_syzygy != 0,
                    NodesSearched: nativeOut.nodes_searched,
                    WallUs: nativeOut.wall_us,
                    DbProbes: nativeOut.db_probes,
                    PositionId: Hash128.FromNative(nativeOut.position_id),
                    MoveId: Hash128.FromNative(nativeOut.move_id),
                    NextPositionId: Hash128.FromNative(nativeOut.next_position_id),
                    ClaimId: Hash128.FromNative(nativeOut.claim_id),
                    BestUci: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.best_uci, 8),
                    BestSan: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.best_san, 16),
                    Source: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.source, 24),
                    Error: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.error, 256));
            }
            finally
            {
                foreach (var p in allocs)
                    Marshal.FreeHGlobal(p);
            }
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public EngineMoldResponse MoldCheckpoint(EngineMoldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutPath);
        _disposeLock.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed || _handle == nint.Zero, this);
            var allocs = new List<nint>();
            try
            {
                byte* AllocZ(string? s)
                {
                    if (s is null) return null;
                    var p = Marshal.StringToCoTaskMemUTF8(s);
                    allocs.Add(p);
                    return (byte*)p;
                }

                LpEngineAuthorityScope nativeAuth = default;
                LpEngineAuthorityScope* authPtr = null;
                if (request.Authority is { } auth)
                {
                    authPtr = &nativeAuth;
                    if (auth.Witnesses is { Count: > 0 } wits)
                    {
                        var pW = Marshal.AllocHGlobal(checked(wits.Count * sizeof(lp_id)));
                        allocs.Add(pW);
                        var witBuf = (lp_id*)pW;
                        for (int i = 0; i < wits.Count; i++)
                            witBuf[i] = wits[i].ToNative();
                        nativeAuth.witnesses = witBuf;
                        nativeAuth.nwitnesses = checked((uint)wits.Count);
                    }
                    nativeAuth.scoped = auth.Scoped ? 1 : 0;
                    nativeAuth.capabilities = auth.Capabilities;
                    nativeAuth.disposition = auth.Disposition;
                    nativeAuth.min_trust = auth.MinTrust;
                    nativeAuth.allow_write = auth.AllowWrite ? 1 : 0;
                }

                var nativeReq = new LpEngineMoldRequest
                {
                    out_path = AllocZ(request.OutPath),
                    model_name = AllocZ(request.ModelName),
                    authority = authPtr,
                    max_vocab = request.MaxVocab,
                    hidden_dim = request.HiddenDim,
                    n_layers = request.NLayers,
                    n_heads = request.NHeads,
                    f16_weights = request.F16Weights ? 1 : 0,
                };

                LpEngineMoldResponse nativeOut = default;
                int rc = LaplaceEngineAbi.lp_engine_mold_checkpoint(_handle, &nativeReq, &nativeOut);
                return new EngineMoldResponse(
                    StatusCode: rc != 0 ? rc : nativeOut.status_code,
                    NVocab: nativeOut.n_vocab,
                    NEdges: nativeOut.n_edges,
                    HiddenDim: nativeOut.hidden_dim,
                    RawWeightBytesRetained: nativeOut.raw_weight_bytes_retained,
                    OutPath: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.out_path, 512),
                    Error: LaplaceEngineAbi.ReadFixedUtf8(nativeOut.error, 256));
            }
            finally
            {
                foreach (var p in allocs)
                    Marshal.FreeHGlobal(p);
            }
        }
        finally
        {
            _disposeLock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        _disposeLock.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != nint.Zero)
            {
                LaplaceEngineAbi.lp_engine_free(_handle);
                _handle = nint.Zero;
            }
        }
        finally
        {
            _disposeLock.ExitWriteLock();
            _disposeLock.Dispose();
        }
    }
}
