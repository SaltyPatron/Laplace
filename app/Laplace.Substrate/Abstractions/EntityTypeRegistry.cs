using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;


public static class EntityTypeRegistry
{
    public static Hash128 Id(string canonicalName) => HighwayPerfcache.NodeHash(canonicalName);

    public static readonly Hash128 Architecture = Id("Architecture");
    public static readonly Hash128 AtomicMarker = Id("Atomic_Marker");
    public static readonly Hash128 AtomicSplit = Id("Atomic_Split");
    public static readonly Hash128 Byte = Id("Byte");
    public static readonly Hash128 CharacterEncoding = Id("CharacterEncoding");
    public static readonly Hash128 CodeConcept = Id("CodeConcept");
    public static readonly Hash128 Codepoint = Id("Codepoint");
    public static readonly Hash128 Document = Id("Document");
    public static readonly Hash128 FrameNetCoreness = Id("FrameNet_Coreness");
    public static readonly Hash128 FrameNetFe = Id("FrameNet_FE");
    public static readonly Hash128 FrameNetFrame = Id("FrameNet_Frame");
    public static readonly Hash128 FrameNetLu = Id("FrameNet_LU");
    public static readonly Hash128 Grapheme = Id("Grapheme");
    public static readonly Hash128 Iso639Code = Id("ISO639Code");
    public static readonly Hash128 Language = Id("Language");
    public static readonly Hash128 LanguageVariant = Id("LanguageVariant");
    public static readonly Hash128 ModelAxis = Id("Model_Axis");
    public static readonly Hash128 ModelLayer = Id("Model_Layer");
    public static readonly Hash128 ModelRecipe = Id("Model_Recipe");
    public static readonly Hash128 ModelTokenizer = Id("Model_Tokenizer");
    public static readonly Hash128 Ngram = Id("Ngram");
    public static readonly Hash128 Ordinal = Id("Ordinal");
    public static readonly Hash128 OrdinalContext = Id("OrdinalContext");
    public static readonly Hash128 Pos = Id("POS");
    public static readonly Hash128 PropBankRoleset = Id("PropBank_Roleset");
    public static readonly Hash128 RepoRoot = Id("RepoRoot");
    public static readonly Hash128 Scalar = Id("Scalar");
    public static readonly Hash128 Sentence = Id("Sentence");
    public static readonly Hash128 SourceFile = Id("SourceFile");
    public static readonly Hash128 TabularColumn = Id("TabularColumn");
    public static readonly Hash128 TabularOutcome = Id("TabularOutcome");
    public static readonly Hash128 TabularValue = Id("TabularValue");
    public static readonly Hash128 TatoebaSentence = Id("Tatoeba_Sentence");
    public static readonly Hash128 Text = Id("Text");
    public static readonly Hash128 UcdClassifier = Id("UcdClassifier");
    public static readonly Hash128 UdFeature = Id("UD_Feature");
    public static readonly Hash128 UdXpos = Id("UD_XPOS");
    public static readonly Hash128 Utf8Role = Id("Utf8Role");
    public static readonly Hash128 VerbNetClass = Id("VerbNet_Class");
    public static readonly Hash128 Word = Id("Word");
    public static readonly Hash128 WordNetSense = Id("WordNet_Sense");
    public static readonly Hash128 WordNetSynset = Id("WordNet_Synset");
}
