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
                : count > 1 ? $"{first.Scope} x{count} (e.g. block {first.Block}{(first.Expert >= 0 ? $" expert {first.Expert}" : "")})"
                : $"{first.Scope} {first.Block}{(first.Expert >= 0 ? $" expert {first.Expert}" : "")}";
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
/// manifest. The residual width comes from the axis frequency, the vocabulary from
/// the tokenizer, block repetition from the paths' numeric segments (a hint), and the
/// remaining symbols from the config. Names only break symmetries between slots of
/// equal shape; an undecided slot is ambiguous, and an unclaimed tensor is
/// unrecognized. Recognition never throws for an unfamiliar architecture.
/// </summary>
public static class ModelOperatorRecognizer
{
    private sealed record Located(HeaderTensor Tensor, int Block, int Expert, bool IsBias)
    {
        public string Name => Tensor.Name;
        public int[] Shape => Tensor.Shape;
    }

    public static ModelAnatomy Recognize(
        IReadOnlyList<HeaderTensor> tensors,
        IReadOnlyDictionary<string, long> config,
        int? tokenizerSize,
        OperatorTemplateManifest? manifest = null)
    {
        ArgumentNullException.ThrowIfNull(tensors);
        ArgumentNullException.ThrowIfNull(config);
        manifest ??= OperatorTemplateManifest.Governed;
        var conflicts = new List<string>();
        var symbols = new Dictionary<string, BoundSymbol>(StringComparer.Ordinal);
        var located = tensors.Select(Locate).ToList();

        long? Declared(OperatorSymbol s)
        {
            foreach (string key in s.ConfigKeys)
                if (config.TryGetValue(key, out long v) && v > 0) return v;
            return null;
        }
        IReadOnlyDictionary<string, long> Values() =>
            symbols.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);

        void Bind(OperatorSymbol s, long value, string basis)
        {
            long? declared = Declared(s);
            symbols[s.Name] = new BoundSymbol(s.Name, value, basis, declared);
            if (declared is { } d && d != value)
                conflicts.Add($"{s.Name}: {basis} gives {value}, config declares {d}");
        }

        // Frequency: the residual width is the axis most tensors share.
        var frequency = new Dictionary<long, int>();
        foreach (HeaderTensor t in tensors)
            foreach (int axis in t.Shape)
                frequency[axis] = frequency.GetValueOrDefault(axis) + 1;
        foreach (OperatorSymbol s in manifest.Symbols.Where(s => s.Bind == SymbolBinding.Frequency))
        {
            if (frequency.Count == 0) break;
            int top = frequency.Values.Max();
            long[] modes = frequency.Where(kv => kv.Value == top).Select(kv => kv.Key).OrderBy(v => v).ToArray();
            if (modes.Length == 1) Bind(s, modes[0], "frequency");
            else if (Declared(s) is { } d && modes.Contains(d)) Bind(s, d, "frequency+config");
            else conflicts.Add($"{s.Name}: axis frequency ties between {string.Join(", ", modes)}");
        }

        // Blocks: the first numeric path segment repeats a structure (a hint).
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
        foreach (OperatorSymbol s in manifest.Symbols.Where(s => s.Bind == SymbolBinding.Config))
        {
            if (Declared(s) is { } v) { Bind(s, v, "config"); continue; }
            if (s.Default is { } expr && ShapeExpression.Parse(expr).Evaluate(Values()) is { } dv)
                Bind(s, dv, $"default {expr}");
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
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var instances = new List<OperatorInstance>();
        var scopes = new List<(string Scope, int Block, int Expert)> { ("model", -1, -1) };
        foreach (int b in located.Where(t => t.Block >= 0).Select(t => t.Block).Distinct().OrderBy(b => b))
        {
            scopes.Add(("block", b, -1));
            foreach (int e in located.Where(t => t.Block == b && t.Expert >= 0).Select(t => t.Expert).Distinct().OrderBy(e => e))
                scopes.Add(("expert", b, e));
        }
        string[] families = manifest.Operators.Select(o => o.Family).Distinct().ToArray();

        foreach ((string scope, int block, int expert) in scopes)
        {
            List<Located> Pool() => located
                .Where(t => !t.IsBias && t.Block == block && t.Expert == expert && !claimed.Contains(t.Name))
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            foreach (string family in families)
            {
                foreach (OperatorTemplate op in manifest.Operators.Where(o => o.Family == family && o.HasScope(scope)))
                {
                    bool fitted = false;
                    while (TryFit(op, Pool(), bound, manifest) is { } fit)
                    {
                        fitted = true;
                        foreach (SlotBinding slot in fit.Slots)
                        {
                            if (slot.Tensor is { } t) claimed.Add(t);
                            foreach (string c in slot.Candidates) claimed.Add(c);
                        }
                        instances.Add(new OperatorInstance(op.Name, op.Family, scope, block, expert,
                            fit.Instance, fit.Slots));
                        if (!op.Repeat) break;
                    }
                    if (fitted) break;
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

    private static Located Locate(HeaderTensor t)
    {
        int block = -1, expert = -1;
        foreach (string segment in t.Name.Split('.'))
        {
            if (segment.Length == 0 || !segment.All(char.IsAsciiDigit)) continue;
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int v)) continue;
            if (block < 0) block = v;
            else if (expert < 0) { expert = v; break; }
        }
        string leaf = t.Name[(t.Name.LastIndexOf('.') + 1)..];
        bool isBias = t.Shape.Length == 1 && leaf.Equals("bias", StringComparison.OrdinalIgnoreCase);
        return new Located(t, block, expert, isBias);
    }

    private sealed record Fit(IReadOnlyList<SlotBinding> Slots, IReadOnlyDictionary<string, long> Instance);

    private static Fit? TryFit(
        OperatorTemplate op, List<Located> pool,
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
