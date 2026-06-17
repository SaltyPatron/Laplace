namespace Laplace.Decomposers.Abstractions;





public static class SafetensorSnapshotWitness
{
    public const string ConfigFile = "config.json";
    public const string TokenizerFile = "tokenizer.json";

    public sealed record ValidationResult(bool Ok, string? Error);

    public static ValidationResult Validate(string snapshotDir)
    {
        if (string.IsNullOrWhiteSpace(snapshotDir) || !Directory.Exists(snapshotDir))
            return new(false, "snapshot directory not found");

        if (!File.Exists(Path.Combine(snapshotDir, ConfigFile)))
            return new(false,
                $"missing {ConfigFile} — safetensors are not self-contained (unlike GGUF); "
                + "architecture recipe lives beside the weight blobs");

        if (!File.Exists(Path.Combine(snapshotDir, TokenizerFile)))
            return new(false,
                $"missing {TokenizerFile} — vocab/merges are not inside .safetensors");

        if (Directory.GetFiles(snapshotDir, "*.safetensors").Length == 0)
            return new(false, "no *.safetensors weight files in snapshot directory");

        return new(true, null);
    }

    public static bool IsComplete(string snapshotDir) => Validate(snapshotDir).Ok;
}
