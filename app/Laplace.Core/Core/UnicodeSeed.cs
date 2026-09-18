namespace Laplace.Engine.Core;

public static unsafe class UnicodeSeed
{
    public const int CodepointCount = 0x110000;

    /// <summary>Read/decompress and parse one UCD XML artifact to completion.</summary>
    public static void ValidateUcdXml(string ucdxmlPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ucdxmlPath);
        int rc = NativeInterop.UnicodeSeedValidateUcdXml(ucdxmlPath);
        if (rc != 0)
            throw new InvalidOperationException(
                $"laplace_unicode_seed_validate_ucdxml(\"{ucdxmlPath}\") returned {rc}");
    }
}
