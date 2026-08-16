using System.Diagnostics;
using System.Text;

namespace Laplace.Agents;

/// <summary>
/// Mints a short-lived credential by running an operator-configured command —
/// the mechanism that makes OAuth and SSO usable on this lane at all.
///
/// Most vendors issue only static API keys, but the ones that do OAuth issue
/// tokens that EXPIRE, so a value pasted into agents.env stops working in an hour
/// and the failure looks like a revoked key. A command re-run per call always
/// yields a live token: <c>ant auth print-credentials --access-token</c> for an
/// Anthropic profile, <c>gcloud auth print-access-token</c> for Vertex, or any
/// script an SSO gateway ships.
///
/// NOT CACHED. One process spawn per call is nothing beside a multi-second model
/// turn, and a cache would need an expiry this layer cannot observe — the token
/// carries its own lifetime and nothing here can read it. A stale token cached
/// behind a working command is the exact failure the command exists to prevent.
///
/// NOT A SHELL. The command is split on whitespace and executed directly, so no
/// pipeline, redirect, or substitution runs. A caller wanting shell semantics
/// names the shell explicitly.
/// </summary>
public static class TokenCommand
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static string Run(string command)
    {
        var argv = Split(command);
        if (argv.Count == 0)
            throw new AgentException("token_command is empty");

        var psi = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);

        using var process = Start(psi, command);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new AgentException(
                $"token_command '{argv[0]}' did not finish within {Timeout.TotalSeconds:0}s and was killed. " +
                "An interactive login prompt will hang here — authenticate once outside Laplace first.");
        }

        if (process.ExitCode != 0)
            throw new AgentException(
                $"token_command '{argv[0]}' exited {process.ExitCode}: " +
                AgentWireFormat.Trim(Tail(stderr.ToString(), stdout.ToString())));

        // Token printers newline-terminate; some also print a trailing blank line.
        var token = stdout.ToString().Trim();
        if (token.Length == 0)
            throw new AgentException(
                $"token_command '{argv[0]}' exited 0 but printed nothing on stdout. " +
                "Commands that print JSON need the flag that emits the bare token — for the Anthropic " +
                "CLI that is `ant auth print-credentials --access-token`, not the bare subcommand.");

        if (token.Contains('\n'))
            throw new AgentException(
                $"token_command '{argv[0]}' printed {token.Split('\n').Length} lines; a credential is one line. " +
                "Check that the command emits the bare token rather than a JSON document.");

        return token;
    }

    private static Process Start(ProcessStartInfo psi, string command)
    {
        try
        {
            return Process.Start(psi)
                ?? throw new AgentException($"token_command '{psi.FileName}' produced no process");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AgentException(
                $"token_command could not run '{psi.FileName}' (from: {command}): {ex.Message}", ex);
        }
    }

    private static string Tail(string stderr, string stdout) =>
        stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();

    /// <summary>
    /// Whitespace split honouring single and double quotes, so a path with a space
    /// or a quoted flag value survives. Deliberately not a shell grammar.
    /// </summary>
    internal static List<string> Split(string command)
    {
        var argv = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var open = false;

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'') { quote = c; open = true; continue; }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || open) { argv.Add(current.ToString()); current.Clear(); open = false; }
                continue;
            }

            current.Append(c);
        }

        if (quote != '\0')
            throw new AgentException($"token_command has an unterminated {quote} quote: {command}");
        if (current.Length > 0 || open) argv.Add(current.ToString());
        return argv;
    }
}
