using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Resolves compute envelopes (#1709) onto C <c>lp_envelope</c> budgets using
/// Laplace-Native presets and per-dimension ceilings (`max_wall_us`, `max_db_probes`,
/// `max_rows_scanned`, `max_tokens_out`).
/// </summary>
internal static class EnvelopeBudgetPolicy
{
    public const int PresetFast = 0;
    public const int PresetStandard = 1;
    public const int PresetDeep = 2;
    public const int PresetUnlimited = 3;

    public const string PresetHeader = "X-Laplace-Envelope";

    internal readonly record struct Resolution(
        lp_envelope Envelope,
        ulong EstimatedUs,
        string PresetName,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public bool Valid => ErrorCode is null;
    }

    public static Resolution Resolve(
        HttpRequest request,
        string? explicitPreset = null,
        ulong? maxWallUs = null,
        ulong? maxDbProbes = null,
        ulong? maxRowsScanned = null,
        uint? maxTokensOut = null,
        int? openaiMaxTokens = null)
    {
        var presetRaw = !string.IsNullOrWhiteSpace(explicitPreset)
            ? explicitPreset.Trim()
            : request.Headers[PresetHeader].ToString().Trim();

        if (!TryParsePreset(presetRaw, out int preset, out string presetName))
            return Invalid("invalid_envelope",
                $"Unknown compute envelope preset '{presetRaw}'. Expected fast, standard, deep, or unlimited.");

        if (maxWallUs == 0 || maxDbProbes == 0 || maxRowsScanned == 0 || maxTokensOut == 0)
            return Invalid("invalid_envelope",
                "Explicit envelope budget bounds must be positive when specified.");

        if (openaiMaxTokens is <= 0)
            return Invalid("invalid_request_error", "max_tokens must be >= 1 when specified.");

        var baseEnv = PresetEnvelope(preset);

        ulong effWall = MinNonZero(baseEnv.max_wall_us, maxWallUs);
        ulong effProbes = MinNonZero(baseEnv.max_db_probes, maxDbProbes);
        ulong effRows = MinNonZero(baseEnv.max_rows_scanned, maxRowsScanned);

        uint? requestedTokens = maxTokensOut;
        if (openaiMaxTokens is > 0)
        {
            uint oai = (uint)openaiMaxTokens.Value;
            requestedTokens = requestedTokens is { } rt ? Math.Min(rt, oai) : oai;
        }
        uint effTokens = MinNonZeroU32(baseEnv.max_tokens_out, requestedTokens);

        var env = new lp_envelope
        {
            max_wall_us = effWall,
            max_db_probes = effProbes,
            max_rows_scanned = effRows,
            max_bytes_alloc = baseEnv.max_bytes_alloc,
            max_hops = baseEnv.max_hops,
            max_tokens_out = effTokens,
            preset = preset,
            reserved = 0,
        };

        ulong estUs = EstimateBudgetUs(env);
        return new Resolution(env, estUs, presetName, null, null);
    }

    public static bool TryParsePreset(string? value, out int preset, out string canonicalName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            preset = PresetStandard;
            canonicalName = "standard";
            return true;
        }
        switch (value.Trim().ToLowerInvariant())
        {
            case "fast" or "quick":
                preset = PresetFast;
                canonicalName = "fast";
                return true;
            case "standard" or "default":
                preset = PresetStandard;
                canonicalName = "standard";
                return true;
            case "deep":
                preset = PresetDeep;
                canonicalName = "deep";
                return true;
            case "unlimited":
                preset = PresetUnlimited;
                canonicalName = "unlimited";
                return true;
            default:
                preset = PresetStandard;
                canonicalName = "standard";
                return false;
        }
    }

    public static long ToBillableUnits(lp_work_counters work, long fallbackUnits = 1)
    {
        if (work.wall_us == 0 && work.db_probes == 0 && work.tokens_out == 0 && work.rows_scanned == 0)
            return Math.Max(1L, fallbackUnits);

        long tokenUnits = Math.Max(1L, (work.tokens_out + 63L) / 64L);
        long probeUnits = Math.Max(1L, (long)((work.db_probes + 7UL) / 8UL));
        return Math.Max(1L, Math.Max(tokenUnits, probeUnits));
    }

    private static lp_envelope PresetEnvelope(int preset)
    {
        try
        {
            return LaplaceNative.lp_envelope_preset(preset);
        }
        catch (DllNotFoundException)
        {
            return preset switch
            {
                PresetFast => new lp_envelope { max_wall_us = 150_000, max_db_probes = 16, max_rows_scanned = 8_192, max_bytes_alloc = 16UL << 20, max_hops = 2, max_tokens_out = 128, preset = PresetFast },
                PresetDeep => new lp_envelope { max_wall_us = 5_000_000, max_db_probes = 256, max_rows_scanned = 262_144, max_bytes_alloc = 256UL << 20, max_hops = 6, max_tokens_out = 2048, preset = PresetDeep },
                PresetUnlimited => new lp_envelope { max_wall_us = 0, max_db_probes = 0, max_rows_scanned = 0, max_bytes_alloc = 0, max_hops = 16, max_tokens_out = 0, preset = PresetUnlimited },
                _ => new lp_envelope { max_wall_us = 1_000_000, max_db_probes = 64, max_rows_scanned = 65_536, max_bytes_alloc = 64UL << 20, max_hops = 4, max_tokens_out = 512, preset = PresetStandard },
            };
        }
    }

    private static unsafe ulong EstimateBudgetUs(lp_envelope env)
    {
        try
        {
            var planned = new lp_work_counters
            {
                db_probes = Math.Min(env.max_db_probes == 0 ? 16UL : env.max_db_probes, 16UL),
                rows_scanned = Math.Min(env.max_rows_scanned == 0 ? 4096UL : env.max_rows_scanned, 4096UL),
                bytes_alloc = 1024UL * 1024UL,
                hops = env.max_hops == 0 ? 4u : Math.Min(env.max_hops, 4u),
                tokens_out = env.max_tokens_out == 0 ? 128u : Math.Min(env.max_tokens_out, 256u),
            };
            var weights = LaplaceNative.lp_work_weights_default();
            return LaplaceNative.lp_envelope_estimate_us(&env, &planned, &weights);
        }
        catch (DllNotFoundException)
        {
            return env.max_wall_us > 0 ? env.max_wall_us : 1_000_000UL;
        }
    }

    private static ulong MinNonZero(ulong presetVal, ulong? reqVal)
    {
        if (reqVal is null) return presetVal;
        if (presetVal == 0) return reqVal.Value;
        return Math.Min(presetVal, reqVal.Value);
    }

    private static uint MinNonZeroU32(uint presetVal, uint? reqVal)
    {
        if (reqVal is null) return presetVal;
        if (presetVal == 0) return reqVal.Value;
        return Math.Min(presetVal, reqVal.Value);
    }

    private static Resolution Invalid(string code, string message) =>
        new(default, 0, "invalid", code, message);
}
