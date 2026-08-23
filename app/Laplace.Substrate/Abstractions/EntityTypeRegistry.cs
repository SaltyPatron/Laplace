using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;


public static class EntityTypeRegistry
{
    public static Hash128 Id(string canonicalName) => HighwayPerfcache.NodeHash(canonicalName);

    public static readonly Hash128 Architecture = Id("Architecture");
    public static readonly Hash128 AtomicMarker = Id("Atomic_Marker");
    public static readonly Hash128 AtomicSplit = Id("Atomic_Split");
    public static readonly Hash128 Byte = Id("Byte");
    public static readonly Hash128 Channel = Id("Channel");
    public static readonly Hash128 CharacterEncoding = Id("CharacterEncoding");
    public static readonly Hash128 CiliConcept = Id("CILI_Concept");
    public static readonly Hash128 CiliInstance = Id("CILI_Instance");
    public static readonly Hash128 CodeConcept = Id("CodeConcept");
    public static readonly Hash128 Codepoint = Id("Codepoint");
    // The entity a set-valued attribute points at: one per distinct member set, by content id.
    public static readonly Hash128 Collection = Id("Collection");
    public static readonly Hash128 ConversationSession = Id("Conversation_Session");
    // Agent-trace lane (spec 34 batch counterpart): a turn is the ordered composition of
    // its part content roots; a tool invocation is the composition of input/result roots.
    public static readonly Hash128 ConversationTurn = Id("Conversation_Turn");
    public static readonly Hash128 AgentTool = Id("Agent_Tool");
    public static readonly Hash128 AgentModel = Id("Agent_Model");
    public static readonly Hash128 ToolInvocation = Id("Tool_Invocation");
    public static readonly Hash128 Document = Id("Document");
    public static readonly Hash128 EsoClass = Id("ESO_Class");
    public static readonly Hash128 EsoRole = Id("ESO_Role");
    public static readonly Hash128 Frame = Id("Frame");
    public static readonly Hash128 FrameNetAnnotation = Id("FrameNet_Annotation");
    public static readonly Hash128 FrameNetAnnotationOccurrence = Id("FrameNet_Annotation_Occurrence");
    public static readonly Hash128 FrameNetCoreness = Id("FrameNet_Coreness");
    public static readonly Hash128 FrameNetFe = Id("FrameNet_FE");
    public static readonly Hash128 FrameNetFrame = Id("FrameNet_Frame");
    public static readonly Hash128 FrameNetLu = Id("FrameNet_LU");
    public static readonly Hash128 Grapheme = Id("Grapheme");
    public static readonly Hash128 Image = Id("Image");
    public static readonly Hash128 Iso639Code = Id("ISO639Code");
    public static readonly Hash128 Language = Id("Language");
    public static readonly Hash128 LanguageVariant = Id("LanguageVariant");
    public static readonly Hash128 ModelAxis = Id("Model_Axis");
    public static readonly Hash128 ModelLayer = Id("Model_Layer");
    public static readonly Hash128 ModelRecipe = Id("Model_Recipe");
    public static readonly Hash128 ModelTokenizer = Id("Model_Tokenizer");
    public static readonly Hash128 McrDomain = Id("MCR_Domain");
    public static readonly Hash128 McrLexname = Id("MCR_Lexname");
    public static readonly Hash128 McrSumo = Id("MCR_SUMO");
    public static readonly Hash128 McrTopOntology = Id("MCR_Top_Ontology");
    public static readonly Hash128 Ngram = Id("Ngram");
    public static readonly Hash128 Number = Id("Number");
    public static readonly Hash128 OnsetSegment = Id("OnsetSegment");
    public static readonly Hash128 OpenSubtitlesAlignment = Id("OpenSubtitles_Alignment");
    public static readonly Hash128 OpenSubtitlesSequence = Id("OpenSubtitles_Sequence");
    public static readonly Hash128 Ordinal = Id("Ordinal");
    public static readonly Hash128 OrdinalContext = Id("OrdinalContext");
    public static readonly Hash128 Patch = Id("Patch");
    public static readonly Hash128 Phrase = Id("Phrase");
    public static readonly Hash128 Pixel = Id("Pixel");
    public static readonly Hash128 Pos = Id("POS");
    public static readonly Hash128 PredicateMatrixAnnotationValue = Id("PredicateMatrix_Annotation_Value");
    public static readonly Hash128 PredicateMatrixPredicate = Id("PredicateMatrix_Predicate");
    public static readonly Hash128 PredicateMatrixRole = Id("PredicateMatrix_Role");
    public static readonly Hash128 PropBankRole = Id("PropBank_Role");
    public static readonly Hash128 PropBankRoleset = Id("PropBank_Roleset");
    public static readonly Hash128 Region = Id("Region");
    public static readonly Hash128 RepoRoot = Id("RepoRoot");
    public static readonly Hash128 Sample = Id("Sample");
    public static readonly Hash128 Scalar = Id("Scalar");
    public static readonly Hash128 Sentence = Id("Sentence");
    public static readonly Hash128 SourceReference = Id("Source_Reference");
    public static readonly Hash128 SourceVersion = Id("Source_Version");
    public static readonly Hash128 SourceFile = Id("SourceFile");
    public static readonly Hash128 TabularColumn = Id("TabularColumn");
    public static readonly Hash128 TabularOutcome = Id("TabularOutcome");
    public static readonly Hash128 TabularValue = Id("TabularValue");
    public static readonly Hash128 Text = Id("Text");
    public static readonly Hash128 Track = Id("Track");
    public static readonly Hash128 Video = Id("Video");
    public static readonly Hash128 UcdClassifier = Id("UcdClassifier");
    public static readonly Hash128 UdFeature = Id("UD_Feature");
    public static readonly Hash128 UdAnnotationMarker = Id("UD_Annotation_Marker");
    public static readonly Hash128 UdAnnotationValue = Id("UD_Annotation_Value");
    public static readonly Hash128 UdParse = Id("UD_Parse");
    public static readonly Hash128 UdParseOccurrence = Id("UD_Parse_Occurrence");
    public static readonly Hash128 UdTokenRef = Id("UD_Token_Ref");
    public static readonly Hash128 UdXpos = Id("UD_XPOS");
    public static readonly Hash128 Utf8Role = Id("Utf8Role");
    public static readonly Hash128 VerbNetClass = Id("VerbNet_Class");
    public static readonly Hash128 VerbNetMember = Id("VerbNet_Member");
    public static readonly Hash128 VerbNetPredicate = Id("VerbNet_Predicate");
    public static readonly Hash128 VerbNetRole = Id("VerbNet_Role");
    // Audio ladder tier 2 — matches native laplace_modality_tier_type_id
    // (blake3("Window")); the audio lanes' "Frame" registration was the split.
    public static readonly Hash128 Window = Id("Window");
    public static readonly Hash128 Word = Id("Word");
    public static readonly Hash128 WordNetSense = Id("WordNet_Sense");
    public static readonly Hash128 WordNetSynset = Id("WordNet_Synset");
    public static readonly Hash128 WikidataItem = Id("Wikidata_Item");
    public static readonly Hash128 WiktionarySense = Id("Wiktionary_Sense");
}
