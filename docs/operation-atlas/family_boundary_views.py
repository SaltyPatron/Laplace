"""Cross-family semantic distinctions, tied to existing contracts and reviewed code."""
def add_family_boundary_views(add):
    add('family-observation-effects','Across families — observation, testimony, completion and effects',r'''left to right direction
package "Monorepo / reviewed source" {
 rectangle "HTTP prompt admission\nInput occurrence + prompt source prior" as MIn
 rectangle "Native forward\nTyped candidate selection\nObligation closure" as MForward
 rectangle "Response occurrence\nResponse source prior\nDEPENDS_ON exact input" as MOut
 rectangle "Novel admitted attestations\nConsensus participant\nSession Projection append" as MWrite
 MIn --> MForward : prompt record returns first
 MForward --> MOut : nonempty reply
 MIn --> MWrite
 MOut --> MWrite
}
package "Modular / reviewed source" {
 rectangle "Engine turn\nPrompt decomposition + scan + chain\nConversation bookkeeping" as ETurn
 rectangle "Engine pull\nRead and print\nTime seed when omitted" as EPull
 rectangle "Native library\nContent identity, text composition\nScalar / series standing calculus" as N
 ETurn --> N
 EPull --> N
}
package "Refactor / declared law" {
 rectangle "Ordinary observation and generated output\nExact content / occurrence / trajectory\nZero semantic testimony merely from observation" as RObserve
 rectangle "Explicit seeded testimony or independently\nobserved/adjudicated outcomes\nTyped standing updates" as RTestify
}
package "Refactor / reviewed framework source" {
 rectangle "Supplied context\nEpochs + authority fingerprint + resource grant" as Context
 rectangle "ISA preflight and instruction execution\nInput / output / execution receipt" as ISA
 rectangle "Provider lifecycle\nSTAGED_INERT -> ACTIVATION_ADMITTED -> ACTIVATED" as Effects
 Context --> ISA
 Context --> Effects
}
note bottom of RObserve
Authority-stack product law is not a claim that all public routes already conform.
end note
note bottom of MWrite
Prompt/response attestations and synthetic HTTP witness events must be
classified against the governing law; method names do not settle that conflict.
end note
note bottom of Effects
ISA execution, cognition completion, evidence admission and effect activation
are different events. Their receipts must not be substituted for one another.
end note''',[
 'Laplace-Refactor/contracts/authority-stack.json',
 'Laplace-Refactor/engine/src/framework.c',
 'Laplace-Refactor/engine/src/isa.c',
 'Laplace/app/Laplace.Substrate/Abstractions/ConversationContent.cs',
 'Laplace/app/Laplace.Substrate/Crud/Npgsql/ConsensusAccumulatingWriter.cs',
 'Laplace/extension/laplace_substrate/src/cognition_program.c',
 'Laplace/app/Laplace.Endpoints.OpenAICompat/TurnWitness.cs',
 ],'Cross-family comparison of explicit law and previously reviewed paths, not a merged execution pipeline. Modular details link to their own source-backed traces. No family is promoted to the definition of the invention, and no code observation overrides inventor authority.',[])
