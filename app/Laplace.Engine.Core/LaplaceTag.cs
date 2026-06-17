namespace Laplace.Engine.Core;


public enum TagType : ushort
{
    Other       = 0,
    Name        = 1,
    DefFunction = 2,
    DefType     = 3,
    DefVar      = 4,
    RefCall     = 5,
    RefType     = 6,
}


public struct LaplaceTag
{
    public uint   MatchId;       
    public ushort CaptureType;   
    public ushort _pad;
    public uint   StartByte;
    public uint   EndByte;
}


public readonly record struct TagCapture(uint MatchId, TagType Type, uint StartByte, uint EndByte);
