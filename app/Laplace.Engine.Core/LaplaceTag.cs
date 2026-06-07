namespace Laplace.Engine.Core;

/// <summary>Normalized tags.scm capture class (mirrors the C LAPLACE_TAG_* enum).</summary>
public enum TagKind : ushort
{
    Other       = 0,
    Name        = 1,
    DefFunction = 2,
    DefType     = 3,
    DefVar      = 4,
    RefCall     = 5,
    RefType     = 6,
}

/// <summary>One tags.scm capture (mirrors C laplace_tag_t, sequential layout).</summary>
public struct LaplaceTag
{
    public uint   MatchId;       // captures of one match share this
    public ushort CaptureKind;   // TagKind
    public ushort _pad;
    public uint   StartByte;
    public uint   EndByte;
}

/// <summary>A managed copy of a capture.</summary>
public readonly record struct TagCapture(uint MatchId, TagKind Kind, uint StartByte, uint EndByte);
