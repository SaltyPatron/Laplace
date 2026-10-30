using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Laplace.Decomposers.Model;

/// <summary>How a template symbol receives its value.</summary>
public enum SymbolBinding
{
    Frequency = 0,
    Tokenizer = 1,
    Blocks = 2,
    Config = 3,
    Instance = 4,
    ConfigOrFrequency = 5,
    ConfigOrShape = 6,
}

public sealed record OperatorSymbol(
    string Name, string Meaning, SymbolBinding Bind,
    IReadOnlyList<string> ConfigKeys, string? Default)
{
    public ShapeExpression? DefaultExpression { get; } = Default is { } text ? ShapeExpression.Parse(text) : null;
}

public sealed record OperatorSlot(
    string Operator, string Role, IReadOnlyList<ShapeExpression> Shape,
    bool EitherOrientation, bool Required, IReadOnlyList<string> Hints,
    SlotConstraint? Where);

/// <param name="Repeat">Whether one scope may hold several instances (factor pairs).</param>
public sealed record OperatorTemplate(
    string Name, string Family, IReadOnlyList<string> Scopes,
    IReadOnlyList<OperatorSlot> Slots, bool Repeat = false)
{
    public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
    public bool AtOwner { get; init; }
    public bool Coexists { get; init; }
    public bool RequireBound { get; init; }
    public bool RequireHints { get; init; }
}

public sealed record OperatorContext(
    IReadOnlyList<string> Paths, string Symbol, IReadOnlyList<string> ConfigKeys, string? Default)
{
    public ShapeExpression? DefaultExpression { get; } = Default is { } text ? ShapeExpression.Parse(text) : null;
    private readonly SourcePathPattern[] _patterns = Paths.Select(SourcePathPattern.Parse).ToArray();

    public IReadOnlyDictionary<string, string>? Match(string sourcePath)
    {
        foreach (SourcePathPattern pattern in _patterns)
            if (pattern.Match(sourcePath) is { } captures) return captures;
        return null;
    }
}

/// <summary>Recipe path globs with named numeric constituents, used to address
/// the corresponding element of a source config array.</summary>
internal sealed class SourcePathPattern
{
    private readonly Regex _pattern;
    private readonly string[] _captures;

    private SourcePathPattern(Regex pattern, string[] captures) { _pattern = pattern; _captures = captures; }

    public static SourcePathPattern Parse(string text)
    {
        var pattern = new StringBuilder("\\A");
        var captures = new List<string>();
        for (int at = 0; at < text.Length; at++)
        {
            char c = text[at];
            if (c == '{')
            {
                int end = text.IndexOf('}', at + 1);
                if (end < 0) throw new InvalidDataException($"context path has an unclosed capture: '{text}'");
                string name = text[(at + 1)..end];
                if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
                    || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')) || captures.Contains(name))
                    throw new InvalidDataException($"context path has an invalid or repeated capture: '{text}'");
                captures.Add(name); pattern.Append("(?<").Append(name).Append(">[0-9]+)"); at = end;
            }
            else if (c == '}') throw new InvalidDataException($"context path has an unmatched closing brace: '{text}'");
            else if (c == '*') pattern.Append(".*");
            else if (c == '?') pattern.Append('.');
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        pattern.Append("\\z");
        return new SourcePathPattern(new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking), captures.ToArray());
    }

    public IReadOnlyDictionary<string, string>? Match(string text)
    {
        Match match = _pattern.Match(text);
        return match.Success ? _captures.ToDictionary(name => name, name => match.Groups[name].Value, StringComparer.Ordinal) : null;
    }

    public static string Expand(string pointer, IReadOnlyDictionary<string, string> captures)
    {
        foreach (var capture in captures) pointer = pointer.Replace("{" + capture.Key + "}", capture.Value, StringComparison.Ordinal);
        if (pointer.Contains('{') || pointer.Contains('}'))
            throw new InvalidDataException($"config pointer uses a capture absent from its source path: '{pointer}'");
        return pointer;
    }
}

public sealed record OperatorScope(string Name, IReadOnlyList<string> After);

/// <summary>
/// The governed operator-template manifest (engine/manifest/model_operators.toml).
/// Shape constrains what a tensor is; names only break symmetries between equal
/// slots. The manifest is embedded in this assembly so recognition never depends
/// on a working directory.
/// </summary>
public sealed class OperatorTemplateManifest
{
    private const string ResourceName = "Laplace.Decomposers.Model.model_operators.toml";
    private static readonly Lazy<OperatorTemplateManifest> Embedded = new(LoadEmbedded);

    public required IReadOnlyList<OperatorSymbol> Symbols { get; init; }
    public required IReadOnlyList<OperatorTemplate> Operators { get; init; }
    public IReadOnlyList<OperatorContext> Contexts { get; init; } = [];
    public IReadOnlyList<OperatorScope> SourceScopes { get; init; } = [];

    public static OperatorTemplateManifest Governed => Embedded.Value;

    private static OperatorTemplateManifest LoadEmbedded()
    {
        using Stream stream = typeof(OperatorTemplateManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded operator manifest '{ResourceName}' is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    public static OperatorTemplateManifest Parse(string toml)
    {
        var tables = TomlTables.Parse(toml);
        var symbols = new List<OperatorSymbol>();
        var operators = new List<(string Name, string Family, List<string> Scopes, bool Repeat, bool AtOwner, bool Coexists, bool RequireBound, bool RequireHints)>();
        var slots = new List<OperatorSlot>();
        var contexts = new List<OperatorContext>();
        var scopes = new List<OperatorScope>();
        foreach ((string kind, Dictionary<string, object> t) in tables)
        {
            switch (kind)
            {
                case "symbol":
                    symbols.Add(new OperatorSymbol(
                        Str(t, "name"), OptStr(t, "meaning") ?? "",
                        ParseBinding(Str(t, "bind")),
                        OptList(t, "config"), OptStr(t, "default")));
                    break;
                case "operator":
                    string locality = OptStr(t, "locality") ?? "scope";
                    if (locality is not ("scope" or "owner"))
                        throw new InvalidDataException($"operator manifest: unknown locality '{locality}'");
                    operators.Add((Str(t, "name"), Str(t, "family"), OptList(t, "scopes").ToList(),
                        t.TryGetValue("repeat", out object? rp) && rp is bool r && r, locality == "owner",
                        t.TryGetValue("coexists", out object? co) && co is true,
                        t.TryGetValue("require_bound", out object? rb) && rb is true,
                        t.TryGetValue("require_hints", out object? rh) && rh is true));
                    break;
                case "context":
                    contexts.Add(new OperatorContext(OptList(t, "paths"), Str(t, "symbol"),
                        OptList(t, "config"), OptStr(t, "default")));
                    break;
                case "scope":
                    scopes.Add(new OperatorScope(Str(t, "name"), OptList(t, "after")));
                    break;
                case "slot":
                    slots.Add(new OperatorSlot(
                        Str(t, "operator"), Str(t, "role"),
                        OptList(t, "shape").Select(ShapeExpression.Parse).ToArray(),
                        string.Equals(OptStr(t, "orientation"), "either", StringComparison.Ordinal),
                        t.TryGetValue("required", out object? req) && req is bool b && b,
                        OptList(t, "hints").Select(h => h.ToLowerInvariant()).ToArray(),
                        OptStr(t, "where") is { } w ? SlotConstraint.Parse(w) : null));
                    break;
                default:
                    throw new InvalidDataException($"operator manifest: unknown table [[{kind}]]");
            }
        }

        var symbolNames = symbols.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var templates = new List<OperatorTemplate>(operators.Count);
        foreach (var op in operators)
        {
            var own = slots.Where(s => s.Operator == op.Name).ToArray();
            if (own.Length == 0)
                throw new InvalidDataException($"operator manifest: operator '{op.Name}' declares no slots");
            foreach (OperatorSlot slot in own)
                foreach (ShapeExpression axis in slot.Shape)
                    foreach (string symbol in axis.Symbols)
                        if (!symbolNames.Contains(symbol))
                            throw new InvalidDataException(
                                $"operator manifest: slot {op.Name}.{slot.Role} uses undeclared symbol '{symbol}'");
            templates.Add(new OperatorTemplate(op.Name, op.Family, op.Scopes, own, op.Repeat)
                { AtOwner = op.AtOwner, Coexists = op.Coexists, RequireBound = op.RequireBound, RequireHints = op.RequireHints });
        }
        foreach (OperatorSlot slot in slots)
            if (!operators.Any(o => o.Name == slot.Operator))
                throw new InvalidDataException($"operator manifest: slot for undeclared operator '{slot.Operator}'");
        foreach (OperatorContext context in contexts)
            if (!symbolNames.Contains(context.Symbol) || context.Paths.Count == 0
                || (context.ConfigKeys.Count == 0 && context.Default is null))
                throw new InvalidDataException($"operator manifest: incomplete context for '{context.Symbol}'");
        var markers = new HashSet<string>(StringComparer.Ordinal);
        foreach (OperatorScope scope in scopes)
        {
            if (scope.After.Count == 0) throw new InvalidDataException($"operator manifest: scope '{scope.Name}' has no path markers");
            foreach (string marker in scope.After)
                if (!markers.Add(marker)) throw new InvalidDataException($"operator manifest: repeated scope marker '{marker}'");
        }
        return new OperatorTemplateManifest { Symbols = symbols, Operators = templates, Contexts = contexts, SourceScopes = scopes };
    }

    private static SymbolBinding ParseBinding(string value) => value switch
    {
        "frequency" => SymbolBinding.Frequency,
        "config-or-frequency" => SymbolBinding.ConfigOrFrequency,
        "config-or-shape" => SymbolBinding.ConfigOrShape,
        "tokenizer" => SymbolBinding.Tokenizer,
        "blocks" => SymbolBinding.Blocks,
        "config" => SymbolBinding.Config,
        "instance" => SymbolBinding.Instance,
        _ => throw new InvalidDataException($"operator manifest: unknown symbol binding '{value}'"),
    };

    private static string Str(Dictionary<string, object> t, string key) =>
        OptStr(t, key) ?? throw new InvalidDataException($"operator manifest: missing '{key}'");

    private static string? OptStr(Dictionary<string, object> t, string key) =>
        t.TryGetValue(key, out object? v) ? v as string : null;

    private static IReadOnlyList<string> OptList(Dictionary<string, object> t, string key) =>
        t.TryGetValue(key, out object? v) && v is List<string> list ? list : Array.Empty<string>();
}

/// <summary>
/// Integer shape expression over template symbols: identifiers, integers,
/// + - * / and parentheses; "_" is a wildcard axis. Division must be exact.
/// </summary>
public sealed class ShapeExpression
{
    private readonly Node _root;

    private ShapeExpression(string text, Node root, bool wildcard)
    {
        Text = text;
        _root = root;
        IsWildcard = wildcard;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        root.Collect(names);
        Symbols = names.ToArray();
    }

    public string Text { get; }
    public bool IsWildcard { get; }
    public IReadOnlyList<string> Symbols { get; }

    public static ShapeExpression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (trimmed == "_") return new ShapeExpression(trimmed, new Constant(0), true);
        int at = 0;
        Node node = ParseSum(trimmed, ref at);
        SkipSpace(trimmed, ref at);
        if (at != trimmed.Length)
            throw new InvalidDataException($"shape expression '{text}' has trailing input at {at}");
        return new ShapeExpression(trimmed, node, false);
    }

    /// <summary>Value under the bound symbols, or null when a symbol is unbound or
    /// the arithmetic is not an exact positive integer.</summary>
    public long? Evaluate(IReadOnlyDictionary<string, long> bound) =>
        IsWildcard ? null : _root.Evaluate(bound) is { } v && v > 0 ? v : null;

    internal KeyValuePair<string, long>? Infer(long size, IReadOnlyDictionary<string, long> bound)
    {
        string[] missing = Symbols.Where(symbol => !bound.ContainsKey(symbol)).ToArray();
        if (IsWildcard || missing.Length != 1 || size <= 0) return null;
        string symbol = missing[0];
        if (_root.Solve(symbol, size, bound) is not { } value || value <= 0) return null;
        var check = new Dictionary<string, long>(bound, StringComparer.Ordinal) { [symbol] = value };
        return Evaluate(check) == size ? new KeyValuePair<string, long>(symbol, value) : null;
    }

    public override string ToString() => Text;

    private static Node ParseSum(string s, ref int at)
    {
        Node left = ParseProduct(s, ref at);
        while (true)
        {
            SkipSpace(s, ref at);
            if (at < s.Length && (s[at] == '+' || s[at] == '-'))
            {
                char op = s[at++];
                Node right = ParseProduct(s, ref at);
                left = new Binary(op, left, right);
            }
            else return left;
        }
    }

    private static Node ParseProduct(string s, ref int at)
    {
        Node left = ParseAtom(s, ref at);
        while (true)
        {
            SkipSpace(s, ref at);
            if (at < s.Length && (s[at] == '*' || s[at] == '/'))
            {
                char op = s[at++];
                Node right = ParseAtom(s, ref at);
                left = new Binary(op, left, right);
            }
            else return left;
        }
    }

    private static Node ParseAtom(string s, ref int at)
    {
        SkipSpace(s, ref at);
        if (at >= s.Length) throw new InvalidDataException($"shape expression '{s}' ends early");
        if (s[at] == '(')
        {
            at++;
            Node inner = ParseSum(s, ref at);
            SkipSpace(s, ref at);
            if (at >= s.Length || s[at] != ')')
                throw new InvalidDataException($"shape expression '{s}' has an unclosed parenthesis");
            at++;
            return inner;
        }
        int start = at;
        if (char.IsDigit(s[at]))
        {
            while (at < s.Length && char.IsDigit(s[at])) at++;
            return new Constant(long.Parse(s.AsSpan(start, at - start), CultureInfo.InvariantCulture));
        }
        if (char.IsLetter(s[at]) || s[at] == '_')
        {
            while (at < s.Length && (char.IsLetterOrDigit(s[at]) || s[at] == '_')) at++;
            return new Symbol(s[start..at]);
        }
        throw new InvalidDataException($"shape expression '{s}' has an unexpected '{s[at]}'");
    }

    private static void SkipSpace(string s, ref int at)
    {
        while (at < s.Length && char.IsWhiteSpace(s[at])) at++;
    }

    private abstract class Node
    {
        public abstract long? Evaluate(IReadOnlyDictionary<string, long> bound);
        public virtual long? Solve(string symbol, long value, IReadOnlyDictionary<string, long> bound) => null;
        public virtual void Collect(ISet<string> names) { }
    }

    private sealed class Constant(long value) : Node
    {
        public override long? Evaluate(IReadOnlyDictionary<string, long> bound) => value;
    }

    private sealed class Symbol(string name) : Node
    {
        public override long? Evaluate(IReadOnlyDictionary<string, long> bound) =>
            bound.TryGetValue(name, out long v) ? v : null;
        public override void Collect(ISet<string> names) => names.Add(name);
        public override long? Solve(string symbol, long value, IReadOnlyDictionary<string, long> bound) =>
            symbol == name ? value : null;
    }

    private sealed class Binary(char op, Node left, Node right) : Node
    {
        public override long? Solve(string symbol, long value, IReadOnlyDictionary<string, long> bound)
        {
            long? a = left.Evaluate(bound), b = right.Evaluate(bound);
            static long? Divide(long numerator, long denominator) =>
                denominator != 0 && numerator % denominator == 0 ? numerator / denominator : null;
            try
            {
                if (a is null && b is { } knownRight)
                {
                    long? target = op switch { '+' => checked(value - knownRight), '-' => checked(value + knownRight),
                        '*' => Divide(value, knownRight), '/' => knownRight != 0 ? checked(value * knownRight) : null, _ => null };
                    return target is { } t ? left.Solve(symbol, t, bound) : null;
                }
                if (b is null && a is { } knownLeft)
                {
                    long? target = op switch { '+' => checked(value - knownLeft), '-' => checked(knownLeft - value),
                        '*' => Divide(value, knownLeft), '/' => Divide(knownLeft, value), _ => null };
                    return target is { } t ? right.Solve(symbol, t, bound) : null;
                }
            }
            catch (OverflowException) { }
            return null;
        }

        public override long? Evaluate(IReadOnlyDictionary<string, long> bound)
        {
            if (left.Evaluate(bound) is not { } a || right.Evaluate(bound) is not { } b) return null;
            try
            {
                return op switch
                {
                    '+' => checked(a + b),
                    '-' => checked(a - b),
                    '*' => checked(a * b),
                    '/' => b != 0 && a % b == 0 ? a / b : null,
                    _ => null,
                };
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        public override void Collect(ISet<string> names)
        {
            left.Collect(names);
            right.Collect(names);
        }
    }
}

/// <summary>A slot constraint of the form "lhs &lt; rhs" or "lhs &lt;= rhs".</summary>
public sealed record SlotConstraint(ShapeExpression Left, string Op, ShapeExpression Right)
{
    public static SlotConstraint Parse(string text)
    {
        foreach (string op in new[] { "<=", ">=", "<", ">", "==" })
        {
            int at = text.IndexOf(op, StringComparison.Ordinal);
            if (at > 0)
                return new SlotConstraint(
                    ShapeExpression.Parse(text[..at]), op, ShapeExpression.Parse(text[(at + op.Length)..]));
        }
        throw new InvalidDataException($"slot constraint '{text}' has no comparison");
    }

    public bool? Holds(IReadOnlyDictionary<string, long> bound)
    {
        if (Left.Evaluate(bound) is not { } a || Right.Evaluate(bound) is not { } b) return null;
        return Op switch
        {
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            "==" => a == b,
            _ => null,
        };
    }
}

/// <summary>
/// The TOML subset the governed manifests use: comments, [[array-of-tables]]
/// headers, and key = string | integer | bool | [string, ...] (arrays may span lines).
/// </summary>
internal static class TomlTables
{
    public static List<(string Kind, Dictionary<string, object> Table)> Parse(string text)
    {
        var tables = new List<(string, Dictionary<string, object>)>();
        Dictionary<string, object>? current = null;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]]", StringComparison.Ordinal))
                    throw new InvalidDataException($"toml line {i + 1}: malformed table header");
                current = new Dictionary<string, object>(StringComparer.Ordinal);
                tables.Add((line[2..^2].Trim(), current));
                continue;
            }
            if (current is null)
                throw new InvalidDataException($"toml line {i + 1}: key outside a table");
            int eq = line.IndexOf('=');
            if (eq <= 0) throw new InvalidDataException($"toml line {i + 1}: expected key = value");
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (value.StartsWith('[') && !BalancedArray(value))
            {
                var sb = new StringBuilder(value);
                while (!BalancedArray(sb.ToString()))
                {
                    if (++i >= lines.Length) throw new InvalidDataException($"toml: unterminated array for '{key}'");
                    sb.Append(' ').Append(StripComment(lines[i]).Trim());
                }
                value = sb.ToString();
            }
            current[key] = ParseValue(value, i + 1);
        }
        return tables;
    }

    private static object ParseValue(string value, int line)
    {
        if (value.StartsWith('"')) return ParseString(value, 0, out _, line);
        if (value == "true") return true;
        if (value == "false") return false;
        if (value.StartsWith('['))
        {
            var items = new List<string>();
            int at = 1;
            while (true)
            {
                while (at < value.Length && (char.IsWhiteSpace(value[at]) || value[at] == ',')) at++;
                if (at >= value.Length) throw new InvalidDataException($"toml line {line}: unterminated array");
                if (value[at] == ']') break;
                if (value[at] != '"') throw new InvalidDataException($"toml line {line}: arrays hold strings only");
                items.Add(ParseString(value, at, out at, line));
            }
            return items;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return n;
        throw new InvalidDataException($"toml line {line}: unsupported value '{value}'");
    }

    private static string ParseString(string s, int start, out int end, int line)
    {
        var sb = new StringBuilder();
        int at = start + 1;
        while (at < s.Length && s[at] != '"')
        {
            if (s[at] == '\\' && at + 1 < s.Length)
            {
                at++;
                sb.Append(s[at] switch { 'n' => '\n', 't' => '\t', _ => s[at] });
            }
            else sb.Append(s[at]);
            at++;
        }
        if (at >= s.Length) throw new InvalidDataException($"toml line {line}: unterminated string");
        end = at + 1;
        return sb.ToString();
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }

    private static bool BalancedArray(string value)
    {
        int depth = 0;
        bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '[') depth++;
            else if (!quoted && c == ']') depth--;
        }
        return depth == 0;
    }
}
