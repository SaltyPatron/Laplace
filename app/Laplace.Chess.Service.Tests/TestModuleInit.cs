using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// The T0 codepoint perfcache is a process-global required by content composition. The openings tests
/// exercise the <c>pgn</c> tree-sitter grammar (native <c>laplace_core</c>); loading the perfcache once
/// here, before any test runs, mirrors the sibling test assemblies and removes per-test ordering
/// fragility. LoadDefault is idempotent. (Native resolution relies on the canonical Windows test env —
/// run via scripts\win\test-app.cmd so build-win\core is on PATH.)
/// </summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init() => CodepointPerfcache.LoadDefault();
}
