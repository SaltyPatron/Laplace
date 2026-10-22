using Serilog;
using Serilog.Events;

namespace Laplace.Ops;

/// <summary>Last-resort diagnostics for failures outside an entrypoint's await/catch
/// boundary. This observes CLR policy; it does not turn a failed task into a
/// successful operation or suppress runtime termination.</summary>
internal static class ProcessFailureEvents
{
    private sealed record Context(string Role, PersistentLogHealth Health);
    private static Context? _context;
    private static int _registered;

    internal static void Register(string role, PersistentLogHealth health)
    {
        Volatile.Write(ref _context, new(role, health));
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    internal static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        var context = Volatile.Read(ref _context);
        if (context is null) return; // Registration publishes the context before attaching the handlers.
        var failure = args.ExceptionObject as Exception ?? new InvalidOperationException(
            $"CLR reported a non-Exception failure object of type {args.ExceptionObject?.GetType().FullName ?? "null"}.");
        LaplaceLogging.ReportProcessFailure("CLR_UNHANDLED_EXCEPTION", failure,
            error => Log.ForContext("SourceContext", "Laplace.Process")
                .ForContext("Application", context.Role).ForContext("IsTerminating", args.IsTerminating)
                .Fatal(error, "CLR_UNHANDLED_EXCEPTION application={ApplicationRole} terminating={IsTerminating}", context.Role, args.IsTerminating),
            context.Health);
        if (args.IsTerminating)
            _ = LaplaceLogging.CompleteProcess(Log.CloseAndFlush, "CLR_UNHANDLED_EXCEPTION", 1, context.Health);
    }

    internal static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        var context = Volatile.Read(ref _context);
        if (context is null) return;
        LaplaceLogging.ReportProcessFailure("UNOBSERVED_TASK_FAILURE", args.Exception,
            error => Log.ForContext("SourceContext", "Laplace.Process")
                .ForContext("Application", context.Role).ForContext("TaskId", (sender as Task)?.Id)
                .Write(LogEventLevel.Error, error, "UNOBSERVED_TASK_FAILURE application={ApplicationRole}", context.Role),
            context.Health);
    }
}
