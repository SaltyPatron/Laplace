namespace Laplace.Decomposers.Model;





public abstract record PathSpec(string RelationName, bool PerLayer);



public record SelfSimilarityPath(
    string RelationName,
    string EmbedPattern)           
    : PathSpec(RelationName, PerLayer: false);




public record BilinearPath(
    string RelationName,
    string LeftPattern,            
    string RightPattern,
    bool RightIsKv = false)
    : PathSpec(RelationName, PerLayer: true);



public record ProjectionPath(
    string RelationName,
    string VPattern,               
    string OPattern)               
    : PathSpec(RelationName, PerLayer: true);




public record ContractionPath(
    string RelationName,
    string? GatePattern,           
    string UpPattern,
    string DownPattern)
    : PathSpec(RelationName, PerLayer: true);
