using System.Globalization;
using System.Text;

namespace Laplace.Ops;

/// <summary>
/// The RFC 4180 CSV shape for the ops.app_log sink — a deliberate SUBSET of ops.pg_log's
/// columns (GH #601) so the two log surfaces read the same way in SQL:
///
///   log_time, application_name, error_severity, category, message, detail
///
/// log_time / application_name / error_severity / message are shared-shape with ops.pg_log;
/// category (the logger category) and detail (exception text) are the app-side columns. A
/// file_fdw foreign table over the sink declares exactly these six columns in this order.
/// No header row (file_fdw reads header 'false'). This static core is what the Serilog
/// <see cref="OpsLogCsvTextFormatter"/> writes per event, kept separate so the exact CSV
/// contract stays unit-testable without a LogEvent.
/// </summary>
public static class OpsLogCsvFormatter
{
    /// <summary>The column order a matching foreign table must declare.</summary>
    public static readonly string[] Columns =
        { "log_time", "application_name", "error_severity", "category", "message", "detail" };

    public static string Format(
        DateTimeOffset when, string applicationName, string severity,
        string category, string message, string? detail)
    {
        var sb = new StringBuilder(128);
        Write(sb, when, applicationName, severity, category, message, detail);
        return sb.ToString();
    }

    internal static void Write(
        StringBuilder sb, DateTimeOffset when, string applicationName, string severity,
        string category, string message, string? detail)
    {
        Field(sb, when.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        sb.Append(',');
        Field(sb, applicationName);
        sb.Append(',');
        Field(sb, severity);
        sb.Append(',');
        Field(sb, category);
        sb.Append(',');
        Field(sb, message);
        sb.Append(',');
        Field(sb, detail ?? string.Empty);
        sb.Append('\n');
    }

    // RFC 4180: quote a field when it holds a comma, quote, CR or LF; escape embedded quotes
    // by doubling. Always quoting would also be valid, but quoting only when needed keeps the
    // common short lines readable in a plain `less`.
    private static void Field(StringBuilder sb, string value)
    {
        bool mustQuote = value.AsSpan().IndexOfAny(",\"\r\n") >= 0;
        if (!mustQuote)
        {
            sb.Append(value);
            return;
        }
        sb.Append('"');
        foreach (char c in value)
        {
            if (c == '"') sb.Append('"');
            sb.Append(c);
        }
        sb.Append('"');
    }
}
