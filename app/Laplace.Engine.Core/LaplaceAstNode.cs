namespace Laplace.Engine.Core;







public unsafe struct LaplaceAstNode
{
    
    public uint NodeTypeId;

    
    public uint StartByte;
    public uint EndByte;

    
    public uint Parent;

    
    public byte IsError;

    private fixed byte _pad[3];
}
