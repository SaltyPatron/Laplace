using System.Globalization;
using System.Text;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace Laplace.Ops;

/// <summary>
/// Serilog text formatter that renders each event as one <see cref="OpsLogCsvFormatter"/> CSV
/// line for the per-role ops.app_log file. The role (application_name) is fixed per logger, so
/// it is passed in rather than read from the event.
/// </summary>
public sealed class OpsLogCsvTextFormatter : ITextFormatter
{
    private static readonly JsonFormatter Detail = new(renderMessage: true, formatProvider: CultureInfo.InvariantCulture);
    private readonly string _role;
    public OpsLogCsvTextFormatter(string role) => _role = role;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        string severity = logEvent.Level switch
        {
            LogEventLevel.Verbose => "TRACE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "LOG",
        };

        string category = "";
        if (logEvent.Properties.TryGetValue("SourceContext", out var src) && src is ScalarValue { Value: string sc })
        {
            category = sc;
        }

        // Keep scopes, operation IDs, trace context and the complete exception chain.
        // The six-column file_fdw contract stays unchanged; detail is structured JSON.
        using var detail = new StringWriter(CultureInfo.InvariantCulture);
        Detail.Format(logEvent, detail);

        var sb = new StringBuilder(128);
        OpsLogCsvFormatter.Write(
            sb, logEvent.Timestamp, _role, severity, category,
            logEvent.RenderMessage(CultureInfo.InvariantCulture), detail.ToString().TrimEnd('\r', '\n'));
        output.Write(sb.ToString());
    }
}
