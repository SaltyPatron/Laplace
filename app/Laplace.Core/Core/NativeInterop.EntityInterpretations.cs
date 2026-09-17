using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "intent_stage_add_entity_interpretation")]
    internal static partial int IntentStageAddEntityInterpretation(
        IntPtr stage, Hash128* id, short tier, Hash128* typeId, Hash128* firstObservedBy);

    [LibraryImport(Library, EntryPoint = "intent_stage_entity_interpretation_count")]
    internal static partial nuint IntentStageEntityInterpretationCount(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_entity_interpretations_complete")]
    internal static partial int IntentStageEntityInterpretationsComplete(IntPtr stage);

    [LibraryImport(Library, EntryPoint = "intent_stage_entity_interpretation_tuple_ptr")]
    internal static partial byte* IntentStageEntityInterpretationTuplePtr(IntPtr stage, nuint* outLength);

    [LibraryImport(Library, EntryPoint = "intent_stage_import_entity_interpretations")]
    internal static partial int IntentStageImportEntityInterpretations(
        IntPtr stage, byte* tuples, nuint bytes);
}
