using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Laplace.Engine.Core;

public static class GrammarDecomposer
{
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".c"] = "c", [".h"] = "c",
        [".cc"] = "cpp", [".cpp"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp", [".hh"] = "cpp",
        [".cs"] = "c-sharp",
        [".py"] = "python",
        [".rs"] = "rust",
        [".go"] = "go",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".ts"] = "typescript", [".tsx"] = "typescript",
        [".sh"] = "bash", [".bash"] = "bash",
        [".json"] = "json",
        [".pgn"] = "pgn",
        [".sql"] = "sql",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".rb"] = "ruby",
        [".jl"] = "julia",
        [".cu"] = "cuda",
        [".f90"] = "fortran",
        [".ll"] = "llvm",
        [".swift"] = "swift",
        [".html"] = "html", [".htm"] = "html",
        [".css"] = "css",
        [".md"] = "markdown",
        [".toml"] = "toml",
        [".yaml"] = "yaml", [".yml"] = "yaml",
    };

    private static readonly string[] KnownModalities =
    [
        "c", "cpp", "c-sharp", "python", "rust", "go", "javascript", "typescript",
        "bash", "json", "pgn", "sql", "java", "kotlin", "ruby", "julia", "cuda",
        "fortran", "llvm", "swift", "html", "css", "markdown", "toml", "yaml",
        "xml", "ps1", "powershell",
    ];

    public static byte[] DecodeJsonStringUtf8(ReadOnlySpan<byte> inner)
    {
        if (inner.IsEmpty) return [];
        var raw = Encoding.UTF8.GetString(inner);
        try
        {
            var decoded = JsonSerializer.Deserialize<string>("\"" + raw + "\"");
            return decoded is null ? [] : Encoding.UTF8.GetBytes(decoded);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("JSON string contains malformed UTF-8 or an invalid escape sequence.");
        }
    }

    public static nint LookupById(string modalityId)
    {
        if (string.IsNullOrWhiteSpace(modalityId)) return IntPtr.Zero;
        int idx = Array.FindIndex(KnownModalities, m => string.Equals(m, modalityId.Trim(), StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? IntPtr.Zero : (nint)(idx + 1);
    }

    public static nint LookupByExt(string ext) =>
        ModalityByExt(ext) is { } m ? LookupById(m) : IntPtr.Zero;

    public static string? ModalityByExt(string ext) =>
        !string.IsNullOrWhiteSpace(ext) && ExtensionMap.TryGetValue(ext.Trim(), out var m) ? m : null;

    public static GrammarAst Parse(ReadOnlySpan<byte> utf8, nint recipe)
    {
        int idx = (int)recipe - 1;
        if ((uint)idx >= (uint)KnownModalities.Length)
            throw new ArgumentException("unknown modality recipe (zero handle)", nameof(recipe));
        return Parse(utf8, KnownModalities[idx]);
    }

    public static GrammarAst Parse(ReadOnlySpan<byte> utf8, string modalityId)
    {
        if (string.IsNullOrWhiteSpace(modalityId) || LookupById(modalityId) == IntPtr.Zero)
            throw new ArgumentException($"unknown modality '{modalityId}'", nameof(modalityId));
        return modalityId.ToLowerInvariant() switch
        {
            "pgn" => ParsePgn(utf8),
            "json" => ParseJson(utf8),
            _ => ParseGeneric(utf8),
        };
    }

    private static GrammarAst ParseJson(ReadOnlySpan<byte> utf8)
    {
        uint err = 0;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            ulong count = 0;
            while (reader.Read()) count++;
            if (reader.BytesConsumed < utf8.Length)
            {
                for (int i = (int)reader.BytesConsumed; i < utf8.Length; i++)
                    if (utf8[i] is not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n') { err = 1; break; }
            }
            var nodes = new List<LaplaceAstNode>
            {
                new() { NodeTypeId = 0, StartByte = 0, EndByte = (uint)utf8.Length, Parent = GrammarAst.Root, IsError = (byte)err },
            };
            return new GrammarAst(["document"], nodes, new GrammarAstDiagnostics(Math.Max(1, count), Math.Max(1, count), err, 0, err));
        }
        catch (JsonException)
        {
            var nodes = new List<LaplaceAstNode>
            {
                new() { NodeTypeId = 0, StartByte = 0, EndByte = (uint)utf8.Length, Parent = GrammarAst.Root, IsError = 1 },
            };
            return new GrammarAst(["document"], nodes, new GrammarAstDiagnostics(1, 1, 1, 0, 1));
        }
    }

    private static GrammarAst ParseGeneric(ReadOnlySpan<byte> utf8)
    {
        var nodes = new List<LaplaceAstNode>
        {
            new() { NodeTypeId = 0, StartByte = 0, EndByte = (uint)utf8.Length, Parent = GrammarAst.Root, IsError = 0 },
        };
        return new GrammarAst(["source_file"], nodes, new GrammarAstDiagnostics(1, 1, 0, 0, 0));
    }

    private static GrammarAst ParsePgn(ReadOnlySpan<byte> utf8)
    {
        string[] names = ["source_file", "variation", "comment", "nag", "annotation", "san_move", "game_result"];
        const uint TSource = 0, TVar = 1, TComment = 2, TNag = 3, TAnn = 4, TSan = 5, TResult = 6;
        var nodes = new List<LaplaceAstNode>
        {
            new() { NodeTypeId = TSource, StartByte = 0, EndByte = (uint)utf8.Length, Parent = GrammarAst.Root, IsError = 0 },
        };
        var parentStack = new Stack<uint>();
        parentStack.Push(0);
        uint errors = 0;

        int i = 0, n = utf8.Length;
        // Skip header tags ([...]) outside movetext cleanly
        while (i < n)
        {
            while (i < n && utf8[i] <= (byte)' ') i++;
            if (i >= n) break;
            if (utf8[i] == (byte)'[')
            {
                i++;
                bool inStr = false;
                while (i < n)
                {
                    byte b = utf8[i++];
                    if (b == (byte)'\\' && inStr && i < n) { i++; continue; }
                    if (b == (byte)'"') { inStr = !inStr; continue; }
                    if (b == (byte)']' && !inStr) break;
                }
                continue;
            }
            break;
        }

        while (i < n)
        {
            while (i < n && utf8[i] <= (byte)' ') i++;
            if (i >= n) break;
            byte b = utf8[i];

            if (b == (byte)';')
            {
                int start = i;
                while (i < n && utf8[i] != (byte)'\n') i++;
                nodes.Add(new LaplaceAstNode { NodeTypeId = TComment, StartByte = (uint)start, EndByte = (uint)i, Parent = parentStack.Peek() });
            }
            else if (b == (byte)'{')
            {
                int start = i++;
                while (i < n && utf8[i] != (byte)'}') i++;
                if (i < n) i++; else errors++;
                nodes.Add(new LaplaceAstNode { NodeTypeId = TComment, StartByte = (uint)start, EndByte = (uint)i, Parent = parentStack.Peek() });
            }
            else if (b == (byte)'(')
            {
                uint vIdx = (uint)nodes.Count;
                nodes.Add(new LaplaceAstNode { NodeTypeId = TVar, StartByte = (uint)i, EndByte = (uint)(i + 1), Parent = parentStack.Peek() });
                parentStack.Push(vIdx);
                i++;
            }
            else if (b == (byte)')')
            {
                if (parentStack.Count > 1)
                {
                    uint vIdx = parentStack.Pop();
                    var vn = nodes[(int)vIdx];
                    vn.EndByte = (uint)(i + 1);
                    nodes[(int)vIdx] = vn;
                }
                else errors++;
                i++;
            }
            else if (b == (byte)'$')
            {
                int start = i++;
                while (i < n && utf8[i] >= (byte)'0' && utf8[i] <= (byte)'9') i++;
                nodes.Add(new LaplaceAstNode { NodeTypeId = TNag, StartByte = (uint)start, EndByte = (uint)i, Parent = parentStack.Peek() });
            }
            else if (b is (byte)'!' or (byte)'?')
            {
                int start = i;
                while (i < n && utf8[i] is (byte)'!' or (byte)'?') i++;
                nodes.Add(new LaplaceAstNode { NodeTypeId = TAnn, StartByte = (uint)start, EndByte = (uint)i, Parent = parentStack.Peek() });
            }
            else if (b == (byte)'*')
            {
                nodes.Add(new LaplaceAstNode { NodeTypeId = TResult, StartByte = (uint)i, EndByte = (uint)(i + 1), Parent = parentStack.Peek() });
                i++;
            }
            else
            {
                int start = i;
                while (i < n && utf8[i] > (byte)' ' && utf8[i] is not (byte)'{' and not (byte)'}' and not (byte)'(' and not (byte)')') i++;
                string tok = Encoding.UTF8.GetString(utf8[start..i]);
                if (tok is "1-0" or "0-1" or "1/2-1/2")
                    nodes.Add(new LaplaceAstNode { NodeTypeId = TResult, StartByte = (uint)start, EndByte = (uint)i, Parent = parentStack.Peek() });
                else if (IsMoveNumber(tok))
                    continue;
                else if (LooksLikeSan(tok))
                    nodes.Add(new LaplaceAstNode { NodeTypeId = TSan, StartByte = (uint)start, EndByte = (uint)i, Parent = parentStack.Peek() });
                else
                    errors++;
            }
        }
        if (parentStack.Count > 1) errors++;
        return new GrammarAst(names, nodes, new GrammarAstDiagnostics((ulong)nodes.Count, (ulong)nodes.Count, errors, 0, errors > 0 ? 1u : 0u));
    }

    private static bool IsMoveNumber(string tok)
    {
        if (tok.Length == 0 || tok[0] < '0' || tok[0] > '9') return false;
        int d = 0;
        while (d < tok.Length && tok[d] is >= '0' and <= '9') d++;
        while (d < tok.Length && tok[d] == '.') d++;
        return d == tok.Length;
    }

    private static bool LooksLikeSan(string tok) =>
        tok.Length >= 2 && (tok[0] is >= 'a' and <= 'h' or 'N' or 'B' or 'R' or 'Q' or 'K' or 'O' or 'P' or 'Z' || tok.Contains('@'));
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct GrammarAstDiagnostics
{
    public readonly ulong AstNodeCount;
    public readonly ulong SyntaxNodeCount;
    public readonly ulong ErrorNodeCount;
    public readonly ulong MissingNodeCount;
    public readonly uint RootHasError;
    private readonly uint _reserved;

    public GrammarAstDiagnostics(ulong astNodeCount, ulong syntaxNodeCount, ulong errorNodeCount, ulong missingNodeCount, uint rootHasError)
    {
        AstNodeCount = astNodeCount;
        SyntaxNodeCount = syntaxNodeCount;
        ErrorNodeCount = errorNodeCount;
        MissingNodeCount = missingNodeCount;
        RootHasError = rootHasError;
        _reserved = 0;
    }

    public bool SyntaxComplete => RootHasError == 0 && ErrorNodeCount == 0 && MissingNodeCount == 0;
}

public sealed unsafe class GrammarAst : IDisposable
{
    public const uint Root = uint.MaxValue;

    private readonly string[] _names;
    private readonly List<LaplaceAstNode> _nodes;
    private readonly GrammarAstDiagnostics _diag;

    internal GrammarAst(string[] names, List<LaplaceAstNode> nodes, GrammarAstDiagnostics diag)
    {
        _names = names;
        _nodes = nodes;
        _diag = diag;
    }

    public nint Handle => 0;
    public int NodeCount => _nodes.Count;
    public GrammarAstDiagnostics Diagnostics => _diag;

    public LaplaceAstNode GetNode(int index)
    {
        if ((uint)index >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _nodes[index];
    }

    public string? NodeTypeName(uint nodeTypeId) =>
        nodeTypeId < (uint)_names.Length ? _names[nodeTypeId] : null;

    public bool NodeTypeIs(uint nodeTypeId, ReadOnlySpan<byte> utf8Name) =>
        NodeTypeName(nodeTypeId) is { } name && Encoding.UTF8.GetByteCount(name) == utf8Name.Length
        && utf8Name.SequenceEqual(Encoding.UTF8.GetBytes(name));

    public void Dispose() { }
}
