using System;
using Laplace.Engine.Core.Ops;
using Xunit;

namespace Laplace.Engine.Core.Tests;

// The CSV shape is a wire contract shared with ops.app_log (file_fdw reads it) — GH #601/#602.
// These pin the column order and RFC 4180 escaping so a schema drift can't silently make the
// foreign table unreadable.
public class OpsLogCsvFormatterTests
{
    private static readonly DateTimeOffset When =
        new(2026, 7, 25, 1, 2, 3, 456, TimeSpan.Zero);

    [Fact]
    public void Columns_AreTheAgreedOrder()
        => Assert.Equal(
            new[] { "log_time", "application_name", "error_severity", "category", "message", "detail" },
            OpsLogCsvFormatter.Columns);

    [Fact]
    public void PlainRecord_IsSixUnquotedFields_NewlineTerminated()
    {
        var line = OpsLogCsvFormatter.Format(When, "uci", "INFO", "session", "uci session started", null);
        Assert.Equal(
            "2026-07-25 01:02:03.456+00:00,uci,INFO,session,uci session started,\n",
            line);
    }

    [Fact]
    public void FieldsWithSeparators_AreQuotedAndQuotesDoubled()
    {
        // message holds a comma, a quote, and a newline — all three must force quoting, and
        // the embedded quote must be doubled per RFC 4180.
        var line = OpsLogCsvFormatter.Format(
            When, "cli", "ERROR", "ingest", "bad row: \"x\",y\nz", "System.Exception: boom");
        Assert.Contains("\"bad row: \"\"x\"\",y\nz\"", line);
        // The exception detail has no separators, so it stays unquoted.
        Assert.EndsWith(",System.Exception: boom\n", line);
    }

    [Fact]
    public void EmptyDetail_IsTrailingEmptyField()
        => Assert.EndsWith(",\n", OpsLogCsvFormatter.Format(When, "mcp", "INFO", "server", "ok", null));
}
