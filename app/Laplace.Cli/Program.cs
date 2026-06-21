using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Atomic2020;
using Laplace.Decomposers.Code;
using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.OMW;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.UD;
using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.FrameNet;
using Laplace.Decomposers.OpenSubtitles;
using Laplace.Decomposers.VerbNet;
using Laplace.Decomposers.PropBank;
using Laplace.Decomposers.SemLink;
using Laplace.Decomposers.Unicode;
using Laplace.Decomposers.WordNet;
using Laplace.Decomposers.Image;
using Laplace.Decomposers.Audio;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Dynamics;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("LAPLACE_SKIP_MKL_CHECK") != "1")
            MklAvailability.EnsureOrThrow();

        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: laplace <command> [args]\n"
                + "  ingest <source> [path]            (unicode | iso639 | wordnet | omw | ud | model)\n"
                + "  synthesize substrate <recipe.json> [output.gguf] [--source-scope <ids>] [--format <name>]\n"
                + "  decompose <text>\n"
                + "  inspect <text>\n"
                + "  converse [prompt]                 (no prompt: REPL — one connection, one session)\n"
                + "  neighbors <word>                  (plural NN: structural geodesic + shape Fréchet + semantic μ)\n"
                + "  walk [prompt]                     (n-gram stride backoff over witnessed trajectories; no prompt: REPL)\n"
                + "  attest <confirm|refute> <tok1> [tok2...]   (OODA feedback: deposit PRECEDES witness for a token sequence)\n"
                + "  roundtrip <file> [out]\n"
                + "  db-roundtrip <file>\n"
                + "  svd-exact-bench [model-dir] [tensor]  (prove tensor_svd_truncate is fp-exact on a real tensor; no DB)\n"
                + "  model-bench [model-dir]              (run the whole-model FFN/relation ETL on a real model; no DB)\n"
                + "  stats");
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "ingest"       => await IngestCommands.IngestAsync(args[1..]),
                "synthesize"   => await FoundryCommands.SynthesizeAsync(args[1..]),
                "decompose"    => DecompositionCommands.Decompose(string.Join(' ', args[1..])),
                "inspect"      => await QueryCommands.InspectAsync(string.Join(' ', args[1..])),
                "converse"     => await QueryCommands.ConverseAsync(string.Join(' ', args[1..])),
                "recall"       => await QueryCommands.RecallAsync(string.Join(' ', args[1..])),
                "neighbors"    => await QueryCommands.NeighborsAsync(string.Join(' ', args[1..])),
                "walk"         => await QueryCommands.WalkAsync(args[1..]),
                "chat"         => await QueryCommands.ChatAsync(args[1..]),
                "attest"       => await QueryCommands.AttestAsync(args[1..]),
                "roundtrip"    => DecompositionCommands.Roundtrip(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : null),
                "db-roundtrip" => await DecompositionCommands.DbRoundtripAsync(args.Length > 1 ? args[1] : ""),
                "stats"        => await IngestCommands.StatsAsync(),
                "indexes"      => await IngestCommands.IndexesAsync(args[1..]),
                "svd-exact-bench" => BenchCommands.SvdExactBenchCmd(args[1..]),
                "model-bench"     => await BenchCommands.ModelBenchCmd(args[1..]),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
