using System.Globalization;
using System.Text;

namespace Laplace.Decomposers.Model;

/// <summary>A tensor as the checkpoint header declares it: path, dtype, shape.</summary>
public sealed record HeaderTensor(string Name, string Dtype, int[] Shape);

/// <summary>How a symbol's value was obtained, with the config's declaration when it had one.</summary>
public sealed record BoundSymbol(string Name, long Value, string Basis, long? Declared);

/// <summary>
/// One slot of a recognized operator instance. <see cref="Basis"/> says what decided
/// it: "shape" (the only tensor of that shape), "shape+hint" (a name hint broke a
/// symmetry between equal slots), "elimination", "ambiguous" (the candidates are
/// listed and none was chosen) or "unbound" (an optional slot with no tensor).
/// </summary>
public sealed record SlotBinding(
    string Role, string? Tensor, IReadOnlyList<string> Candidates,
    string Orientation, string Basis, string? Bias = null)
{
    public bool IsBound => Tensor is not null;
    public bool IsAmbiguous => Basis == "ambiguous";
}

/// <summary>One recognized operator at one source-scoped location (model, block, expert).</summary>
public sealed record OperatorInstance(
    string Operator, string Family, string Scope, int Block, int Expert,
    IReadOnlyDictionary<string, long> InstanceSymbols,
    IReadOnlyList<SlotBinding> Slots)
{
    public SlotBinding? Slot(string role) => Slots.FirstOrDefault(s => s.Role == role);
    /// <summary>The full source container holding this operator's operands.</summary>
    public string SourcePath { get; init; } = "";
    /// <summary>Recipe symbols evaluated in this source scope, including its own config bindings.</summary>
    public IReadOnlyDictionary<string, long> ContextSymbols { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// The checkpoint's recognized source anatomy: bound symbols, operator instances,
/// every tensor no template claimed, and every disagreement between the shapes and
/// the config. It is computed from header shapes and config only, before any write.
/// </summary>
public sealed record ModelAnatomy(
    IReadOnlyDictionary<string, BoundSymbol> Symbols,
    IReadOnlyList<OperatorInstance> Operators,
    IReadOnlyList<HeaderTensor> Unrecognized,
    IReadOnlyList<string> Conflicts,
    int TensorCount)
{
    public long? Symbol(string name) => Symbols.TryGetValue(name, out BoundSymbol? s) ? s.Value : null;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"tensors: {TensorCount}");
        sb.AppendLine("symbols:");
        foreach (BoundSymbol s in Symbols.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {s.Name,-5} = {s.Value,-7} ({s.Basis}{(s.Declared is { } d && d != s.Value ? $"; config declares {d}" : "")})");
        foreach (var byFamily in Operators
                     .GroupBy(o => (o.Scope, o.Operator))
                     .OrderBy(g => g.Key.Scope == "model" ? 0 : g.Key.Scope == "block" ? 1 : 2))
        {
            OperatorInstance first = byFamily.First();
            int count = byFamily.Count();
            string where = first.Scope == "model" ? "model"
                : count > 1 ? $"{first.Scope} x{count} (e.g. {first.SourcePath})"
                : first.SourcePath;
            sb.AppendLine(CultureInfo.InvariantCulture, $"operator {first.Operator} [{first.Family}] @ {where}");
            if (first.InstanceSymbols.Count > 0)
                sb.AppendLine("    instance: " + string.Join(", ",
                    first.InstanceSymbols.Select(kv => $"{kv.Key}={kv.Value}")));
            foreach (SlotBinding slot in first.Slots)
            {
                string target = slot.Tensor
                    ?? (slot.Candidates.Count > 0 ? "{" + string.Join(" | ", slot.Candidates) + "}" : "-");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    {slot.Role,-16} {slot.Basis,-12} {slot.Orientation,-7} {target}{(slot.Bias is null ? "" : $" + bias {slot.Bias}")}");
            }
            int irregular = byFamily.Count(o => o.Slots.Any(s => s.IsAmbiguous));
            if (irregular > 0)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    ambiguous in {irregular} of {count} instances");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"unrecognized: {Unrecognized.Count}");
        foreach (HeaderTensor t in Unrecognized)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {t.Name} [{string.Join(",", t.Shape)}]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"conflicts: {Conflicts.Count}");
        foreach (string c in Conflicts) sb.AppendLine("  " + c);
        return sb.ToString();
    }
}

/// <summary>
/// Recognizes a checkpoint's operators by shape against the governed template
/// manifest. The manifest declares config/frequency precedence, the vocabulary comes from
/// the tokenizer, source scopes from recipe-declared path markers, and the
/// remaining symbols from the config. Names only break symmetries between slots of
/// equal shape; an undecided slot is ambiguous, and an unclaimed tensor is
/// unrecognized. Recognition never throws for an unfamiliar architecture.
/// </summary>
public static class ModelOperatorRecognizer
{
    private sealed record Located(HeaderTensor Tensor, int Block, int Expert, bool IsBias,
        string Scope, string Path)
    {
        public string Name => Tensor.Name;
        public int[] Shape => Tensor.Shape;
    }

    public static ModelAnatomy Recognize(
        IReadOnlyList<HeaderTensor> tensors,
        IReadOnlyDictionary<string, long> config,
        int? tokenizerSize,
        OperatorTemplateManifest? manifest = null) => Recognize(tensors, config, tokenizerSize, manifest, null);

    public static ModelAnatomy Recognize(
        IReadOnlyList<HeaderTensor> tensors,
        IReadOnlyDictionary<string, long> config,
        int? tokenizerSize,
        OperatorTemplateManifest? manifest,
        IReadOnlyDictionary<string, long>? jsonConfig)
    {
        ArgumentNullException.ThrowIfNull(tensors);
        ArgumentNullException.ThrowIfNull(config);
        manifest ??= OperatorTemplateManifest.Governed;
        var conflicts = new List<string>();
        var symbols = new Dictionary<string, BoundSymbol>(StringComparer.Ordinal);
        var located = tensors.Select(t => Locate(t, manifest.SourceScopes)).ToList();

        long? Declared(OperatorSymbol s)
        {
            foreach (string key in s.ConfigKeys)
                if (Configured(key) is { } v) return v;
            return null;
        }
        long? Configured(string key) =>
            key.StartsWith('/') && jsonConfig is not null
                ? jsonConfig.TryGetValue(key, out long nested) && nested > 0 ? nested : null
                : config.TryGetValue(key, out long value) && value > 0 ? value : null;
        IReadOnlyDictionary<string, long> Values() =>
            symbols.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);

        void Bind(OperatorSymbol s, long value, string basis)
        {
            long? declared = Declared(s);
            symbols[s.Name] = new BoundSymbol(s.Name, value, basis, declared);
            if (declared is { } d && d != value)
                conflicts.Add($"{s.Name}: {basis} gives {value}, config declares {d}");
        }

        // Binding precedence is part of the recipe. A multimodal package's most
        // frequent axis need not be the width its config declares.
        var frequency = new Dictionary<long, int>();
        foreach (HeaderTensor t in tensors)
            foreach (int axis in t.Shape)
                frequency[axis] = frequency.GetValueOrDefault(axis) + 1;
        foreach (OperatorSymbol s in manifest.Symbols.Where(s => s.Bind is SymbolBinding.Frequency or SymbolBinding.ConfigOrFrequency))
        {
            if (s.Bind == SymbolBinding.ConfigOrFrequency && Declared(s) is { } declared)
            {
                Bind(s, declared, "config");
                if (!frequency.ContainsKey(declared))
                    conflicts.Add($"{s.Name}: config declares {declared}, which no tensor axis carries");
                continue;
            }
            if (frequency.Count == 0) break;
            int top = frequency.Values.Max();
            long[] modes = frequency.Where(kv => kv.Value == top).Select(kv => kv.Key).OrderBy(v => v).ToArray();
            if (modes.Length == 1) Bind(s, modes[0], "frequency");
            else if (Declared(s) is { } d && modes.Contains(d)) Bind(s, d, "frequency+config");
            else conflicts.Add($"{s.Name}: axis frequency ties between {string.Join(", ", modes)}");
        }

        // Block ordinals come from the source path fields the recipe names.
        foreach (OperatorSymbol s in manifest.Symbols.Where(s => s.Bind == SymbolBinding.Blocks))
        {
            var blocks = located.Where(t => t.Block >= 0).Select(t => t.Block).Distinct().OrderBy(b => b).ToArray();
            if (blocks.Length == 0) continue;
            if (blocks[0] != 0 || blocks[^1] != blocks.Length - 1)
                conflicts.Add($"{s.Name}: block indices are not contiguous from 0 ({blocks[0]}..{blocks[^1]}, {blocks.Length} distinct)");
            Bind(s, blocks.Length, "blocks");
        }

        // Tokenizer: the vocabulary axis is the smallest model-scope axis paired with d
        // that covers the tokenizer's id space.
        long? width = symbols.TryGetValue("d", out BoundSymbol? dSym) ? dSym.Value : null;
        foreach (OperatorSymbol s in manifest.Symbols.Where(s => s.Bind == SymbolBinding.Tokenizer))
        {
            if (tokenizerSize is not { } vt || vt <= 0)
            {
                if (Declared(s) is { } dv) Bind(s, dv, "config");
                continue;
            }
            long? rows = width is { } d
                ? located.Where(t => t.Block < 0 && t.Shape.Length == 2)
                    .SelectMany(t => t.Shape[1] == d ? [t.Shape[0]] : t.Shape[0] == d ? new long[] { t.Shape[1] } : [])
                    .Where(x => x >= vt).DefaultIfEmpty(-1).Min()
                : null;
            if (rows is { } x && x > 0)
            {
                Bind(s, x, x == vt ? "tokenizer" : $"tokenizer covers {vt} of {x} rows");
            }
            else
            {
                conflicts.Add($"{s.Name}: no model-scope axis paired with d covers the tokenizer's {vt} ids");
                Bind(s, vt, "tokenizer");
            }
        }

        // Config: the first declared key, else the declared default expression.
        foreach (OperatorSymbol s in manifest.Symbols.Where(s => s.Bind is SymbolBinding.Config or SymbolBinding.ConfigOrShape))
        {
            if (Declared(s) is { } v) { Bind(s, v, "config"); continue; }
            if (s.DefaultExpression?.Evaluate(Values()) is { } dv)
                Bind(s, dv, $"default {s.Default}");
        }

        // A width that no tensor carries is re-solved from block shapes: the
        // non-residual axis shared by a [x, d] and a [d, x] tensor in every block.
        if (symbols.TryGetValue("f", out BoundSymbol? fSym) && width is { } dw
            && !frequency.ContainsKey(fSym.Value) && !frequency.ContainsKey(2 * fSym.Value))
        {
            long? solved = located.Where(t => t.Block >= 0 && t.Shape.Length == 2 && t.Shape[1] == dw && t.Shape[0] != dw)
                .Select(t => (long)t.Shape[0])
                .Where(x => located.Any(u => u.Block >= 0 && u.Shape.Length == 2 && u.Shape[0] == dw && u.Shape[1] == x))
                .GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => (long?)g.Key).FirstOrDefault();
            if (solved is { } sf)
            {
                conflicts.Add($"f: {fSym.Basis} gives {fSym.Value}, which no tensor carries; block shapes give {sf}");
                symbols["f"] = fSym with { Value = sf, Basis = "shape" };
            }
        }

        IReadOnlyDictionary<string, long> bound = Values();
        IReadOnlyDictionary<string, long> ContextValues(string path)
        {
            var values = new Dictionary<string, long>(bound, StringComparer.Ordinal);
            var supplied = new HashSet<string>(StringComparer.Ordinal);
            foreach (OperatorContext context in manifest.Contexts)
            {
                if (context.Match(path) is not { } captures) continue;
                long? value = context.ConfigKeys.Select(key => Configured(SourcePathPattern.Expand(key, captures)))
                    .FirstOrDefault(n => n is not null);
                value ??= context.DefaultExpression?.Evaluate(values);
                if (value is { } n) { values[context.Symbol] = n; supplied.Add(context.Symbol); }
            }
            foreach (OperatorSymbol symbol in manifest.Symbols.Where(s => (s.Bind is SymbolBinding.Config or SymbolBinding.ConfigOrShape)
                         && s.Default is not null && !supplied.Contains(s.Name) && Declared(s) is null))
            {
                if (symbol.DefaultExpression?.Evaluate(values) is { } n) values[symbol.Name] = n;
                else values.Remove(symbol.Name);
            }
            return values;
        }
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var instances = new List<OperatorInstance>();
        var sourcePools = located.Where(t => !t.IsBias)
            .GroupBy(t => (t.Scope, t.Path))
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray());
        string[] families = manifest.Operators.Select(o => o.Family).Distinct().ToArray();

        foreach (var entry in sourcePools.OrderBy(p => p.Key.Scope == "model" ? 0 : 1)
                     .ThenBy(p => p.Key.Path, StringComparer.Ordinal))
        {
            (string scope, string path) = entry.Key;
            Located[] sourcePool = entry.Value;
            int block = sourcePool[0].Block, expert = sourcePool[0].Expert;
            IReadOnlyDictionary<string, long> context = ContextValues(path);
            var owners = OwnerPools(path, sourcePool);
            foreach (string family in families)
            {
                OperatorTemplate[] templates = manifest.Operators.Where(o => o.Family == family && o.HasScope(scope)).ToArray();
                foreach (var owner in templates.Any(o => o.AtOwner) ? owners : [(Path: path, Pool: sourcePool)])
                {
                    foreach (OperatorTemplate op in templates.Where(o => o.AtOwner || owner.Path == path))
                    {
                        bool fitted = false;
                        while (TryFit(op, owner.Pool.Where(t => !claimed.Contains(t.Name)).ToList(), context, manifest) is { } fit)
                        {
                            fitted = true;
                            foreach (SlotBinding slot in fit.Slots)
                            {
                                if (slot.Tensor is { } t) claimed.Add(t);
                                foreach (string c in slot.Candidates) claimed.Add(c);
                            }
                            var instanceContext = new Dictionary<string, long>(context, StringComparer.Ordinal);
                            foreach (var value in fit.Instance) instanceContext[value.Key] = value.Value;
                            instances.Add(new OperatorInstance(op.Name, op.Family, scope, block, expert,
                                fit.Instance, fit.Slots) { SourcePath = owner.Path, ContextSymbols = instanceContext });
                            if (!op.Repeat) break;
                        }
                        if (fitted && !op.Coexists) break;
                    }
                }
            }
        }

        // A bias is a sibling of a recognized slot (same path, "bias" leaf) whose
        // length equals that slot's output axis.
        var byName = new Dictionary<string, (int Instance, int Slot)>(StringComparer.Ordinal);
        for (int i = 0; i < instances.Count; i++)
            for (int j = 0; j < instances[i].Slots.Count; j++)
                if (instances[i].Slots[j].Tensor is { } t) byName[t] = (i, j);
        var tensorByName = tensors.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (Located bias in located.Where(t => t.IsBias))
        {
            string prefix = bias.Name[..^"bias".Length];
            if (!byName.TryGetValue(prefix + "weight", out var at)
                && !byName.TryGetValue(prefix.TrimEnd('.', '_'), out at))
                continue;
            OperatorInstance inst = instances[at.Instance];
            SlotBinding slot = inst.Slots[at.Slot];
            int[] shape = tensorByName[slot.Tensor!].Shape;
            int output = shape.Length == 1 ? shape[0]
                : slot.Orientation == "in,out" ? shape[^1] : shape[0];
            if (bias.Shape.Length != 1 || bias.Shape[0] != output) continue;
            var slots = inst.Slots.ToArray();
            slots[at.Slot] = slot with { Bias = bias.Name };
            instances[at.Instance] = inst with { Slots = slots };
            claimed.Add(bias.Name);
        }

        var unrecognized = tensors.Where(t => !claimed.Contains(t.Name)).ToArray();
        return new ModelAnatomy(symbols, instances, unrecognized, conflicts, tensors.Count);
    }

    private static Func<string, bool> IsInstanceSymbol(OperatorTemplateManifest manifest)
    {
        var set = manifest.Symbols.Where(s => s.Bind == SymbolBinding.Instance)
            .Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        return set.Contains;
    }

    private static (string Path, Located[] Pool)[] OwnerPools(string scope, Located[] tensors)
    {
        var owners = new Dictionary<string, List<Located>>(StringComparer.Ordinal);
        foreach (Located tensor in tensors)
        {
            string path = tensor.Name;
            while (path.LastIndexOf('.') is int dot && dot >= scope.Length)
            {
                path = path[..dot];
                if (!owners.TryGetValue(path, out List<Located>? pool)) owners[path] = pool = [];
                pool.Add(tensor);
                if (path == scope) break;
            }
        }
        if (!owners.ContainsKey(scope)) owners[scope] = tensors.ToList();
        return owners.OrderByDescending(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (p.Key, p.Value.ToArray())).ToArray();
    }

    private static Located Locate(HeaderTensor t, IReadOnlyList<OperatorScope> scopes)
    {
        int block = -1, expert = -1;
        string sourceScope = "model", sourcePath = "";
        int end = 0;
        string? previous = null;
        foreach (string segment in t.Name.Split('.'))
        {
            int segmentEnd = end + segment.Length; end = segmentEnd + 1;
            OperatorScope? declaration = previous is null ? null : scopes.FirstOrDefault(s => s.After.Contains(previous, StringComparer.Ordinal));
            if (declaration is not null && segment.Length > 0 && segment.All(char.IsAsciiDigit)
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int v))
            {
                sourceScope = declaration.Name; sourcePath = t.Name[..segmentEnd];
                if (sourceScope == "block") block = v;
                if (sourceScope == "expert") expert = v;
            }
            previous = segment;
        }
        string leaf = t.Name[(t.Name.LastIndexOf('.') + 1)..];
        bool isBias = t.Shape.Length == 1 && leaf.Equals("bias", StringComparison.OrdinalIgnoreCase);
        return new Located(t, block, expert, isBias, sourceScope, sourcePath);
    }

    private sealed record Fit(IReadOnlyList<SlotBinding> Slots, IReadOnlyDictionary<string, long> Instance);

    private static Fit? TryFit(
        OperatorTemplate op, List<Located> pool,
        IReadOnlyDictionary<string, long> bound, OperatorTemplateManifest manifest)
    {
        var inferred = InferShapeSymbols(op, pool, bound, manifest);
        if (inferred.Count > 0)
        {
            var resolved = new Dictionary<string, long>(bound, StringComparer.Ordinal);
            foreach (var value in inferred) resolved[value.Key] = value.Value;
            Fit? fit = TryFitBound(op, pool, resolved, manifest);
            if (fit is null) return null;
            foreach (var value in fit.Instance) inferred[value.Key] = value.Value;
            return fit with { Instance = inferred };
        }
        return TryFitBound(op, pool, bound, manifest);
    }

    private static Dictionary<string, long> InferShapeSymbols(OperatorTemplate op, List<Located> pool,
        IReadOnlyDictionary<string, long> bound, OperatorTemplateManifest manifest)
    {
        var permitted = manifest.Symbols.Where(s => s.Bind == SymbolBinding.ConfigOrShape && !bound.ContainsKey(s.Name))
            .Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var inferred = new Dictionary<string, long>(StringComparer.Ordinal);
        if (permitted.Count == 0) return inferred;
        var values = new Dictionary<string, long>(bound, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (OperatorSlot slot in op.Slots)
            {
                var candidates = new List<(int Hint, Dictionary<string, long> Values)>();
                foreach (Located tensor in pool.Where(t => t.Shape.Length == slot.Shape.Count))
                    foreach (bool reversed in slot.EitherOrientation && slot.Shape.Count == 2 ? new[] { false, true } : new[] { false })
                    {
                        var local = new Dictionary<string, long>(values, StringComparer.Ordinal);
                        bool matches = true;
                        for (int axis = 0; axis < slot.Shape.Count && matches; axis++)
                        {
                            int size = tensor.Shape[reversed ? 1 - axis : axis];
                            ShapeExpression expression = slot.Shape[axis];
                            if (expression.IsWildcard) continue;
                            if (expression.Evaluate(local) is { } expected) matches = expected == size;
                            else if (expression.Symbols.All(local.ContainsKey)) matches = false;
                            else if (expression.Infer(size, local) is { } proposal && permitted.Contains(proposal.Key))
                                local[proposal.Key] = proposal.Value;
                        }
                        if (matches && slot.Where?.Holds(local) != false)
                            candidates.Add((HintLength(slot, tensor.Name), local));
                    }
                if (candidates.Count == 0) continue;
                int hint = candidates.Max(candidate => candidate.Hint);
                if (!slot.Required && hint == 0) continue;
                var supported = candidates.Where(candidate => candidate.Hint == hint).ToArray();
                foreach (string symbol in permitted.Where(symbol => !values.ContainsKey(symbol)))
                {
                    if (supported.Any(candidate => !candidate.Values.ContainsKey(symbol))) continue;
                    long[] proposals = supported.Select(candidate => candidate.Values[symbol]).Distinct().ToArray();
                    if (proposals.Length != 1) continue;
                    values[symbol] = inferred[symbol] = proposals[0]; changed = true;
                }
            }
        } while (changed);
        return inferred;
    }

    private static Fit? TryFitBound(OperatorTemplate op, List<Located> pool,
        IReadOnlyDictionary<string, long> bound, OperatorTemplateManifest manifest)
    {
        Func<string, bool> isInstance = IsInstanceSymbol(manifest);
        var instanceSymbols = op.Slots.SelectMany(s => s.Shape).SelectMany(a => a.Symbols)
            .Where(isInstance).Distinct().ToArray();
        if (instanceSymbols.Length == 0)
            return FitBound(op, pool, bound, new Dictionary<string, long>());

        // Unify instance symbols from the first slot that names one bare, trying
        // candidates in path order.
        OperatorSlot? anchor = op.Slots.FirstOrDefault(s => s.Shape.Any(a => a.Symbols.Count == 1
            && a.Text == a.Symbols[0] && isInstance(a.Symbols[0])));
        if (anchor is null) return null;
        foreach (Located candidate in pool.Where(t => t.Shape.Length == anchor.Shape.Count))
        {
            foreach (bool reversed in anchor.EitherOrientation && anchor.Shape.Count == 2
                         ? new[] { false, true } : new[] { false })
            {
                int[] shape = reversed ? [candidate.Shape[1], candidate.Shape[0]] : candidate.Shape;
                var local = new Dictionary<string, long>(bound, StringComparer.Ordinal);
                var instance = new Dictionary<string, long>(StringComparer.Ordinal);
                for (int i = 0; i < anchor.Shape.Count; i++)
                {
                    ShapeExpression axis = anchor.Shape[i];
                    if (!axis.IsWildcard && axis.Symbols.Count == 1 && axis.Text == axis.Symbols[0]
                        && isInstance(axis.Symbols[0]))
                    {
                        local[axis.Text] = shape[i];
                        instance[axis.Text] = shape[i];
                    }
                }
                bool ok = true;
                for (int i = 0; i < anchor.Shape.Count && ok; i++)
                    if (!anchor.Shape[i].IsWildcard && anchor.Shape[i].Evaluate(local) != shape[i]) ok = false;
                if (!ok || anchor.Where?.Holds(local) == false) continue;
                if (FitBound(op, pool, local, instance) is { } fit) return fit;
            }
        }
        return null;
    }

    private static Fit? FitBound(
        OperatorTemplate op, List<Located> pool,
        IReadOnlyDictionary<string, long> bound, Dictionary<string, long> instance)
    {
        var expected = new Dictionary<OperatorSlot, long?[]>();
        foreach (OperatorSlot slot in op.Slots)
        {
            long?[] axes = slot.Shape.Select(a => a.IsWildcard ? (long?)null : a.Evaluate(bound)).ToArray();
            bool evaluable = slot.Shape.Select((a, i) => a.IsWildcard || axes[i] is not null).All(x => x);
            if (!evaluable || slot.Where?.Holds(bound) == false)
            {
                if (slot.Required) return null;
                continue;
            }
            expected[slot] = axes;
        }

        var candidates = new Dictionary<OperatorSlot, List<(Located Tensor, string Orientation)>>();
        foreach ((OperatorSlot slot, long?[] axes) in expected)
        {
            var list = new List<(Located, string)>();
            foreach (Located t in pool)
            {
                if (t.Shape.Length != axes.Length) continue;
                if (op.RequireHints && slot.Required && HintLength(slot, t.Name) == 0) continue;
                bool direct = Matches(t.Shape, axes, reversed: false);
                bool transposed = slot.EitherOrientation && axes.Length == 2 && Matches(t.Shape, axes, reversed: true);
                if (!direct && !transposed) continue;
                string orientation = axes.Length != 2 ? "-"
                    : direct && transposed ? (slot.EitherOrientation ? "square" : "out,in")
                    : direct ? "out,in" : "in,out";
                list.Add((t, orientation));
            }
            candidates[slot] = list;
        }

        // A required operand needs its own tensor. A gated operator cannot claim
        // a plain two-matrix MLP merely because gate and up both match one row.
        var owners = new Dictionary<string, OperatorSlot>(StringComparer.Ordinal);
        bool AssignRequired(OperatorSlot slot, HashSet<string> visited)
        {
            foreach (var candidate in candidates[slot])
            {
                if (!visited.Add(candidate.Tensor.Name)) continue;
                if (!owners.TryGetValue(candidate.Tensor.Name, out OperatorSlot? owner)
                    || AssignRequired(owner, visited))
                {
                    owners[candidate.Tensor.Name] = slot; return true;
                }
            }
            return false;
        }
        foreach (OperatorSlot required in op.Slots.Where(s => s.Required))
            if (!AssignRequired(required, new HashSet<string>(StringComparer.Ordinal))) return null;

        // Slots that share candidates are symmetric: resolve each connected group
        // by shape, then name hints, then elimination; otherwise leave it ambiguous.
        var result = new Dictionary<OperatorSlot, SlotBinding>();
        var pending = expected.Keys.ToList();
        while (pending.Count > 0)
        {
            var group = new List<OperatorSlot> { pending[0] };
            var tensorsInGroup = new HashSet<string>(candidates[pending[0]].Select(c => c.Tensor.Name), StringComparer.Ordinal);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (OperatorSlot other in pending.Where(s => !group.Contains(s)).ToList())
                {
                    if (candidates[other].Any(c => tensorsInGroup.Contains(c.Tensor.Name)))
                    {
                        group.Add(other);
                        foreach (var c in candidates[other]) tensorsInGroup.Add(c.Tensor.Name);
                        grew = true;
                    }
                }
            }
            pending.RemoveAll(group.Contains);
            ResolveGroup(group, candidates, result);
        }

        foreach (OperatorSlot slot in op.Slots)
        {
            if (!result.TryGetValue(slot, out SlotBinding? binding) || (!binding.IsBound && !binding.IsAmbiguous))
            {
                if (slot.Required) return null;
                result[slot] = new SlotBinding(slot.Role, null, [], "-", "unbound");
            }
        }
        if (op.RequireBound && op.Slots.Any(slot => slot.Required && !result[slot].IsBound)) return null;
        // An operator is present when a slot is decided, or when a required slot has
        // candidates it cannot choose between; ambiguity over optional slots alone
        // is no evidence of the operator.
        if (!result.Values.Any(b => b.IsBound)
            && !op.Slots.Any(s => s.Required && result[s].IsAmbiguous))
            return null;
        return new Fit(op.Slots.Select(s => result[s]).ToArray(), instance);
    }

    private static void ResolveGroup(
        List<OperatorSlot> group,
        Dictionary<OperatorSlot, List<(Located Tensor, string Orientation)>> candidates,
        Dictionary<OperatorSlot, SlotBinding> result)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var open = new List<OperatorSlot>(group);

        void Assign(OperatorSlot slot, (Located Tensor, string Orientation) c, string basis)
        {
            result[slot] = new SlotBinding(slot.Role, c.Tensor.Name, [], c.Orientation, basis);
            taken.Add(c.Tensor.Name);
            open.Remove(slot);
        }

        List<(Located Tensor, string Orientation)> Free(OperatorSlot s) =>
            candidates[s].Where(c => !taken.Contains(c.Tensor.Name)).ToList();

        if (group.Count == 1 && candidates[group[0]].Count == 1)
        {
            Assign(group[0], candidates[group[0]][0], "shape");
            return;
        }

        // Hints decide most-specific first: a (slot, tensor) pairing whose matched hint
        // is longer than every competing pairing for that slot and that tensor is
        // assigned; an equal-length tie decides nothing.
        var matches = new List<(OperatorSlot Slot, (Located Tensor, string Orientation) Candidate, int Length)>();
        foreach (OperatorSlot slot in group)
            foreach (var c in candidates[slot])
                if (HintLength(slot, c.Tensor.Name) is > 0 and int length)
                    matches.Add((slot, c, length));
        foreach (var m in matches.OrderByDescending(m => m.Length).ToList())
        {
            if (!open.Contains(m.Slot) || taken.Contains(m.Candidate.Tensor.Name)) continue;
            bool tie = matches.Any(o =>
                o.Length == m.Length
                && open.Contains(o.Slot) && !taken.Contains(o.Candidate.Tensor.Name)
                && !(ReferenceEquals(o.Slot, m.Slot) && o.Candidate.Tensor.Name == m.Candidate.Tensor.Name)
                && (ReferenceEquals(o.Slot, m.Slot) || o.Candidate.Tensor.Name == m.Candidate.Tensor.Name));
            if (tie) continue;
            Assign(m.Slot, m.Candidate, "shape+hint");
        }
        if (open.Count == 0) return;

        // A symmetric group with fewer tensors than required slots is not this operator.
        var remainingTensors = open.SelectMany(Free).Select(c => c.Tensor.Name).Distinct().Count();
        if (open.Count(s => s.Required) > remainingTensors)
        {
            foreach (OperatorSlot slot in open.Where(s => s.Required))
                result[slot] = new SlotBinding(slot.Role, null, [], "-", "infeasible");
            return;
        }

        var free = open.SelectMany(Free).DistinctBy(c => c.Tensor.Name).ToList();
        if (free.Count == 0) return;
        var required = open.Where(s => s.Required).ToList();
        if (open.Count == 1 && free.Count == 1)
        {
            Assign(open[0], free[0], "elimination");
            return;
        }
        if (required.Count == 1 && free.Count == 1 && Free(required[0]).Count == 1)
        {
            Assign(required[0], free[0], "elimination");
            return;
        }
        foreach (OperatorSlot slot in open)
        {
            var mine = Free(slot).Select(c => c.Tensor.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (mine.Length > 0)
                result[slot] = new SlotBinding(slot.Role, null, mine, "-", "ambiguous");
        }
    }

    private static int HintLength(OperatorSlot slot, string name)
    {
        string lower = name.ToLowerInvariant();
        int best = 0;
        foreach (string hint in slot.Hints)
            if (hint.Length > best && lower.Contains(hint, StringComparison.Ordinal)) best = hint.Length;
        return best;
    }

    private static bool Matches(int[] shape, long?[] axes, bool reversed)
    {
        for (int i = 0; i < axes.Length; i++)
        {
            long? want = axes[reversed ? axes.Length - 1 - i : i];
            if (want is { } w && shape[i] != w) return false;
        }
        return true;
    }
}
