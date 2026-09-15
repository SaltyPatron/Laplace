-- Execution requires a witnessed whole-observation call and its bound input.
-- Naming an operation is semantic evidence; it does not establish a request.
\set ECHO none
BEGIN;
CREATE FUNCTION pg_temp.admit_operational_prompt(p_prompt text,p_source bytea)
RETURNS void LANGUAGE sql AS $admit$
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT DISTINCT p.id,p.tier,
           laplace.entity_type_id(CASE p.tier
               WHEN 0 THEN 'Codepoint' WHEN 1 THEN 'Grapheme'
               WHEN 2 THEN 'Word' WHEN 3 THEN 'Sentence' ELSE 'Document' END),
           p_source
      FROM converse.prompt_tree(p_prompt,false) p
    ON CONFLICT (id,tier) DO NOTHING;
$admit$;

-- Controlled source testimony plus pooled standing. The actual operation is
-- always run by generation.forward_program, never by a fixture interpreter.
CREATE FUNCTION pg_temp.operation_cell(
    p_subject bytea,p_type bytea,p_object bytea,p_source bytea,p_context bytea)
RETURNS void LANGUAGE plpgsql AS $cell$
BEGIN
    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,context_id,outcome,
         last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES
        (public.laplace_hash128_blake3(
             p_subject || p_type || p_object || p_source || COALESCE(p_context,''::bytea)),
         p_subject,p_type,p_object,p_source,p_context,2,
         now(),5,5000000000,30000000000);
    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES
        (laplace.consensus_id(p_subject,p_type,p_object),p_subject,p_type,p_object,
         2000000000000,30000000000,60000000,5,now())
    ON CONFLICT (id,type_id,subject_id) DO NOTHING;
END
$cell$;

CREATE FUNCTION pg_temp.operation_receipt(
    p_prompt text,p_steps int DEFAULT 2,p_output bytea[] DEFAULT NULL,p_context bytea DEFAULT NULL,p_fanout int DEFAULT 32,p_hops int DEFAULT 0)
RETURNS TABLE(emitted bytea[],complete boolean,disposition text,families int,program_id bytea,remaining int)
LANGUAGE plpgsql AS $receipt$
BEGIN
    IF p_context IS NULL THEN
        RETURN QUERY
    SELECT array_agg(p.entity ORDER BY p.step) FILTER (WHERE p.event='emit'),
           bool_or(p.completion),
           (array_agg(p.disposition ORDER BY p.step DESC)
                FILTER (WHERE p.event IN ('complete','unresolved')))[1],
           max(p.relation_families) FILTER (WHERE p.event='emit'),
           (array_agg(p.program_id ORDER BY p.step DESC))[1],
           (array_agg(p.remaining_required ORDER BY p.step DESC)
                FILTER (WHERE p.event IN ('complete','unresolved')))[1]
      FROM generation.forward_program(p_prompt,p_steps,0,0.0,16,7,p_hops,p_fanout,NULL,p_output) p;
    ELSE
        RETURN QUERY
    SELECT array_agg(p.entity ORDER BY p.step) FILTER (WHERE p.event='emit'),
           bool_or(p.completion),
           (array_agg(p.disposition ORDER BY p.step DESC)
                FILTER (WHERE p.event IN ('complete','unresolved')))[1],
           max(p.relation_families) FILTER (WHERE p.event='emit'),
           (array_agg(p.program_id ORDER BY p.step DESC))[1],
           (array_agg(p.remaining_required ORDER BY p.step DESC)
                FILTER (WHERE p.event IN ('complete','unresolved')))[1]
      FROM generation.forward_program(p_prompt,p_steps,0,0.0,16,7,p_hops,p_fanout,NULL,p_output,p_context) p;
    END IF;
END
$receipt$;
CREATE FUNCTION pg_temp.no_operation(p_label text,p_prompt text,p_context bytea DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $absent$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_temp.operation_receipt(p_prompt,p_context=>p_context);
    IF r.emitted IS NOT NULL OR r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: % executed without a grounded call/input: emitted=% complete=%',
            p_label,r.emitted,r.complete;
    END IF;
END
$absent$;

DO $operational_memory$
DECLARE
    prompt text := 'zarquiv seedoperandx';
    cue_id bytea := laplace.word_id('zarquiv');
    operand_id bytea := laplace.word_id('seedoperandx');
    answer_id bytea := public.laplace_hash128_blake3('test/operational-memory/answer');
    changed_answer bytea := public.laplace_hash128_blake3('test/operational-memory/changed-answer');
    definition_id bytea := public.laplace_hash128_blake3('test/operational-memory/definition');
    source_id bytea := public.laplace_hash128_blake3('test/operational-memory/source');
    other_source bytea := public.laplace_hash128_blake3('test/operational-memory/other-source');
    context_id bytea := public.laplace_hash128_blake3('test/operational-memory/lesson');
    other_context bytea := public.laplace_hash128_blake3('test/operational-memory/other-lesson');
    causes_id bytea := laplace.relation_type_id('CAUSES');
    defines_id bytea := laplace.relation_type_id('HAS_DEFINITION');
    names_id bytea := laplace.relation_type_id('HAS_NAME_ALIAS');
    related_id bytea := laplace.relation_type_id('RELATED_TO');
    calls_id bytea := laplace.relation_type_id('CALLS');
    input_id bytea := laplace.relation_type_id('HAS_INPUT');
    prompt_root bytea;
    first_program bytea;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by) VALUES
        (source_id,2,laplace.entity_type_id('Source'),source_id),
        (other_source,2,laplace.entity_type_id('Source'),other_source),
        (context_id,2,laplace.entity_type_id('Source_Reference'),source_id),
        (other_context,2,laplace.entity_type_id('Source_Reference'),source_id),
        (answer_id,2,laplace.entity_type_id('CodeConcept'),source_id),
        (changed_answer,2,laplace.entity_type_id('CodeConcept'),source_id),
        (definition_id,2,laplace.entity_type_id('CodeConcept'),source_id);
    PERFORM pg_temp.admit_operational_prompt(prompt,source_id);
    SELECT p.root_id INTO prompt_root FROM converse.prompt_tree(prompt) p LIMIT 1;

    PERFORM pg_temp.operation_cell(operand_id,causes_id,answer_id,source_id,context_id);
    PERFORM pg_temp.operation_cell(operand_id,related_id,answer_id,source_id,context_id);
    PERFORM pg_temp.no_operation('unannotated observation',prompt,context_id);
    PERFORM pg_temp.operation_cell(causes_id,names_id,cue_id,source_id,context_id);
    PERFORM pg_temp.no_operation('alias plus available result',prompt,context_id);

    -- The source explicitly witnesses what the complete observation invokes.
    -- CALLS alone still supplies no operand binding.
    PERFORM pg_temp.operation_cell(prompt_root,calls_id,causes_id,source_id,context_id);
    PERFORM pg_temp.no_operation('call without input',prompt,context_id);

    -- Input testimony from another context must not be combined with the call.
    PERFORM pg_temp.operation_cell(prompt_root,input_id,operand_id,source_id,other_context);
    PERFORM pg_temp.no_operation('mismatched context',prompt,context_id);
    DELETE FROM laplace.attestations
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    PERFORM pg_temp.operation_cell(prompt_root,input_id,operand_id,other_source,context_id);
    PERFORM pg_temp.no_operation('mismatched source',prompt,context_id);
    DELETE FROM laplace.attestations
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    PERFORM pg_temp.operation_cell(prompt_root,input_id,operand_id,source_id,NULL);
    PERFORM pg_temp.no_operation('missing occurrence context',prompt,context_id);
    DELETE FROM laplace.attestations
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;

    -- Stored content-root testimony does not itself make a new user observation
    -- an invocation. The caller must explicitly bind the matching context.
    PERFORM pg_temp.operation_cell(prompt_root,input_id,operand_id,source_id,context_id);
    PERFORM pg_temp.no_operation('natural observation with a stored contract',prompt);
    PERFORM pg_temp.no_operation('wrong supplied invocation context',prompt,other_context);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,p_context=>context_id);
    IF r.emitted IS DISTINCT FROM ARRAY[answer_id] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: complete witnessed call/input did not execute: %',r;
    END IF;
    IF r.families IS NULL OR r.families < 2 THEN
        RAISE EXCEPTION 'FAIL: operation hid another responding relation family: %',r.families;
    END IF;
    first_program := r.program_id;

    -- Changing the current result cell changes the actual output, while the
    -- invocation testimony remains byte-for-byte unchanged.
    DELETE FROM laplace.consensus
     WHERE subject_id=operand_id AND type_id=causes_id AND object_id=answer_id;
    PERFORM pg_temp.operation_cell(operand_id,causes_id,changed_answer,source_id,context_id);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,p_context=>context_id);
    IF r.emitted IS DISTINCT FROM ARRAY[changed_answer] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: execution replayed a canned result instead of current operand state: %',r;
    END IF;

    -- Naming the operation elsewhere, reordering the same words, and mentioning
    -- the original phrase produce different whole-observation identities.
    PERFORM pg_temp.admit_operational_prompt('seedoperandx zarquiv',source_id);
    PERFORM pg_temp.no_operation('changed word order','seedoperandx zarquiv',context_id);
    PERFORM pg_temp.admit_operational_prompt('The words zarquiv seedoperandx occur here',source_id);
    PERFORM pg_temp.no_operation('mention inside another observation','The words zarquiv seedoperandx occur here',context_id);

    UPDATE laplace.attestations SET outcome=0,sum_score_fp1e9=0
     WHERE subject_id=prompt_root AND type_id=calls_id AND object_id=causes_id;
    PERFORM pg_temp.no_operation('refuted call testimony',prompt,context_id);
    UPDATE laplace.attestations SET outcome=2,sum_score_fp1e9=5000000000
     WHERE subject_id=prompt_root AND type_id=calls_id AND object_id=causes_id;
    UPDATE laplace.attestations SET observation_count=0
     WHERE subject_id=prompt_root AND type_id=calls_id AND object_id=causes_id;
    PERFORM pg_temp.no_operation('zero-observation call',prompt,context_id);
    UPDATE laplace.attestations SET observation_count=5
     WHERE subject_id=prompt_root AND type_id=calls_id AND object_id=causes_id;
    UPDATE laplace.attestations SET outcome=0,sum_score_fp1e9=0
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    PERFORM pg_temp.no_operation('refuted input testimony',prompt,context_id);
    UPDATE laplace.attestations SET outcome=2,sum_score_fp1e9=5000000000
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    UPDATE laplace.consensus SET rating=1000000000000
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    PERFORM pg_temp.no_operation('negative input standing',prompt,context_id);
    UPDATE laplace.consensus SET rating=2000000000000
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    DELETE FROM laplace.consensus
     WHERE subject_id=prompt_root AND type_id=calls_id AND object_id=causes_id;
    PERFORM pg_temp.no_operation('withdrawn call standing',prompt,context_id);
    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES
        (laplace.consensus_id(prompt_root,calls_id,causes_id),prompt_root,calls_id,causes_id,
         2000000000000,30000000000,60000000,5,now());
    DELETE FROM laplace.attestations
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=operand_id;
    PERFORM pg_temp.no_operation('withdrawn input testimony',prompt,context_id);
    PERFORM pg_temp.operation_cell(prompt_root,input_id,operand_id,source_id,context_id);

    -- Alternative explicit calls stay ambiguous; available result popularity
    -- cannot silently choose which act the source meant.
    PERFORM pg_temp.operation_cell(prompt_root,calls_id,defines_id,source_id,context_id);
    PERFORM pg_temp.operation_cell(operand_id,defines_id,definition_id,source_id,context_id);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,p_context=>context_id);
    IF r.complete IS DISTINCT FROM false OR r.disposition IS DISTINCT FROM 'ambiguous' THEN
        RAISE EXCEPTION 'FAIL: alternative whole-root calls lost ambiguity: %',r;
    END IF;
    IF first_program IS NULL OR r.program_id IS NULL OR first_program=r.program_id THEN
        RAISE EXCEPTION 'FAIL: program identity omitted its changed invocation contract';
    END IF;

    -- The existing public forward-program output mask remains a caller-owned
    -- relation read. It does not select one conflicting witnessed invocation.
    PERFORM pg_temp.operation_cell(prompt_root,defines_id,definition_id,source_id,context_id);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,ARRAY[defines_id]);
    IF r.emitted IS DISTINCT FROM ARRAY[definition_id] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: inferred alternatives overrode explicit caller output: %',r;
    END IF;
    RAISE NOTICE 'operational memory: aliases do not invoke; active context and sourced call/input execute current state; incompatible evidence is rejected';
END
$operational_memory$;

DO $separate_input_obligations$
DECLARE
    prompt text := 'seedinputalpha seedinputbeta';
    first_input bytea := laplace.word_id('seedinputalpha');
    second_input bytea := laplace.word_id('seedinputbeta');
    first_answer bytea := public.laplace_hash128_blake3('test/operational-memory/pair-first');
    second_answer bytea := public.laplace_hash128_blake3('test/operational-memory/pair-second');
    source_id bytea := public.laplace_hash128_blake3('test/operational-memory/pair-source');
    context_id bytea := public.laplace_hash128_blake3('test/operational-memory/pair-context');
    causes_id bytea := laplace.relation_type_id('CAUSES');
    calls_id bytea := laplace.relation_type_id('CALLS');
    input_id bytea := laplace.relation_type_id('HAS_INPUT');
    prompt_root bytea;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by) VALUES
        (source_id,2,laplace.entity_type_id('Source'),source_id),
        (context_id,2,laplace.entity_type_id('Source_Reference'),source_id),
        (first_answer,2,laplace.entity_type_id('CodeConcept'),source_id),
        (second_answer,2,laplace.entity_type_id('CodeConcept'),source_id);
    PERFORM pg_temp.admit_operational_prompt(prompt,source_id);
    SELECT p.root_id INTO prompt_root FROM converse.prompt_tree(prompt) p LIMIT 1;
    PERFORM pg_temp.operation_cell(prompt_root,calls_id,causes_id,source_id,context_id);
    PERFORM pg_temp.operation_cell(prompt_root,input_id,first_input,source_id,context_id);
    PERFORM pg_temp.operation_cell(prompt_root,input_id,second_input,source_id,context_id);
    PERFORM pg_temp.operation_cell(first_input,causes_id,first_answer,source_id,context_id);
    PERFORM pg_temp.operation_cell(second_input,causes_id,second_answer,source_id,context_id);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,p_context=>context_id);
    IF cardinality(r.emitted) IS DISTINCT FROM 1 OR r.complete IS DISTINCT FROM false OR r.remaining <= 0 THEN
        RAISE EXCEPTION 'FAIL: one input result discharged both declared inputs: %',r;
    END IF;
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,2,p_context=>context_id);
    IF cardinality(r.emitted) IS DISTINCT FROM 2
       OR NOT r.emitted @> ARRAY[first_answer,second_answer]
       OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: declared input set did not complete after both actual results: %',r;
    END IF;
    -- Candidate budgets do not shrink the invocation's required input set.
    DELETE FROM laplace.consensus
     WHERE subject_id=second_input AND type_id=causes_id AND object_id=second_answer;
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,2,p_context=>context_id,p_fanout=>1);
    IF r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: low fanout discarded an input whose result was unavailable: %',r;
    END IF;
    UPDATE laplace.consensus SET rating=1000000000000
     WHERE subject_id=prompt_root AND type_id=input_id AND object_id=second_input;
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,2,p_context=>context_id,p_fanout=>1);
    IF r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: low fanout hid negative declared input standing: %',r;
    END IF;
    RAISE NOTICE 'operational memory: each declared input retains its own completion obligation';
END
$separate_input_obligations$;

DO $admitted_operation_relation$
DECLARE
    prompt text := 'λ seedoperanddynamic';
    operand_id bytea := laplace.word_id('seedoperanddynamic');
    answer_id bytea := public.laplace_hash128_blake3('test/operational-memory/dynamic-answer');
    source_id bytea := public.laplace_hash128_blake3('test/operational-memory/dynamic-source');
    context_id bytea := public.laplace_hash128_blake3('test/operational-memory/dynamic-context');
    operation_id bytea := public.laplace_hash128_blake3('test/operational-memory/dynamic-operation');
    calls_id bytea := laplace.relation_type_id('CALLS');
    input_id bytea := laplace.relation_type_id('HAS_INPUT');
    prompt_root bytea;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by) VALUES
        (source_id,2,laplace.entity_type_id('Source'),source_id),
        (context_id,2,laplace.entity_type_id('Source_Reference'),source_id),
        (operation_id,2,laplace.entity_type_id('RelationType'),source_id),
        (answer_id,2,laplace.entity_type_id('CodeConcept'),source_id);
    PERFORM pg_temp.admit_operational_prompt(prompt,source_id);
    SELECT p.root_id INTO prompt_root FROM converse.prompt_tree(prompt) p LIMIT 1;
    PERFORM pg_temp.operation_cell(prompt_root,calls_id,operation_id,source_id,context_id);
    PERFORM pg_temp.operation_cell(prompt_root,input_id,operand_id,source_id,context_id);
    PERFORM pg_temp.operation_cell(operand_id,operation_id,answer_id,source_id,context_id);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,p_context=>context_id);
    IF r.emitted IS DISTINCT FROM ARRAY[answer_id] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: explicit sourced invocation required compiled operation spelling: %',r;
    END IF;
    RAISE NOTICE 'operational memory: an admitted relation executes without compiled lexical names';
END
$admitted_operation_relation$;
-- Unicode surfaces and semantic identities are different coordinates. These
-- two source-witnessed surfaces bind the same semantic input and operation.
DO $multilingual_semantic_input$
DECLARE
    first_prompt text := 'eau';
    second_prompt text := '水';
    first_surface bytea := laplace.word_id(first_prompt);
    second_surface bytea := laplace.word_id(second_prompt);
    semantic_input bytea := public.laplace_hash128_blake3('test/operational-memory/shared-semantic-input');
    alternate_input bytea := public.laplace_hash128_blake3('test/operational-memory/alternate-semantic-input');
    answer_id bytea := public.laplace_hash128_blake3('test/operational-memory/shared-semantic-answer');
    alternate_answer bytea := public.laplace_hash128_blake3('test/operational-memory/alternate-semantic-answer');
    source_id bytea := public.laplace_hash128_blake3('test/operational-memory/multilingual-source');
    alternate_source bytea := public.laplace_hash128_blake3('test/operational-memory/alternate-semantic-source');
    first_context bytea := public.laplace_hash128_blake3('test/operational-memory/first-language-context');
    second_context bytea := public.laplace_hash128_blake3('test/operational-memory/second-language-context');
    calls_id bytea := laplace.relation_type_id('CALLS');
    input_id bytea := laplace.relation_type_id('HAS_INPUT');
    senses_id bytea := laplace.relation_type_id('HAS_SENSE');
    operation_id bytea := laplace.relation_type_id('CAUSES');
    first_root bytea;
    second_root bytea;
    first_result bytea[];
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by) VALUES
        (source_id,2,laplace.entity_type_id('Source'),source_id),
        (first_context,2,laplace.entity_type_id('Source_Reference'),source_id),
        (second_context,2,laplace.entity_type_id('Source_Reference'),source_id),
        (semantic_input,2,laplace.entity_type_id('CodeConcept'),source_id),
        (alternate_source,2,laplace.entity_type_id('Source'),alternate_source),
        (alternate_input,2,laplace.entity_type_id('CodeConcept'),alternate_source),
        (alternate_answer,2,laplace.entity_type_id('CodeConcept'),alternate_source),
        (answer_id,2,laplace.entity_type_id('CodeConcept'),source_id);
    PERFORM pg_temp.admit_operational_prompt(first_prompt,source_id);
    PERFORM pg_temp.admit_operational_prompt(second_prompt,source_id);
    SELECT p.root_id INTO first_root FROM converse.prompt_tree(first_prompt) p LIMIT 1;
    SELECT p.root_id INTO second_root FROM converse.prompt_tree(second_prompt) p LIMIT 1;
    IF first_root=second_root OR semantic_input=first_surface OR semantic_input=second_surface THEN
        RAISE EXCEPTION 'FAIL: multilingual fixture collapsed surface and semantic identities';
    END IF;
    PERFORM pg_temp.operation_cell(first_surface,senses_id,semantic_input,source_id,first_context);
    PERFORM pg_temp.operation_cell(second_surface,senses_id,semantic_input,source_id,second_context);
    PERFORM pg_temp.operation_cell(first_root,calls_id,operation_id,source_id,first_context);
    PERFORM pg_temp.operation_cell(second_root,calls_id,operation_id,source_id,second_context);
    PERFORM pg_temp.operation_cell(first_root,input_id,semantic_input,source_id,first_context);
    PERFORM pg_temp.operation_cell(second_root,input_id,semantic_input,source_id,second_context);
    PERFORM pg_temp.operation_cell(semantic_input,operation_id,answer_id,source_id,first_context);

    SELECT * INTO r FROM pg_temp.operation_receipt(first_prompt,1,p_context=>first_context,p_hops=>1);
    IF r.emitted IS DISTINCT FROM ARRAY[answer_id] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: first surface did not execute its shared semantic input: %',r;
    END IF;
    first_result := r.emitted;
    SELECT * INTO r FROM pg_temp.operation_receipt(second_prompt,1,p_context=>second_context,p_hops=>1);
    IF r.emitted IS DISTINCT FROM first_result OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: Unicode surface changed the declared semantic operation result: %',r;
    END IF;
    RAISE NOTICE 'operational memory: distinct Unicode surfaces execute the same canonical semantic input and operation';

    -- A single scoped call can require two canonical inputs reached from the
    -- same surface occurrence. Completing one identity cannot discharge both.
    PERFORM pg_temp.operation_cell(first_surface,senses_id,alternate_input,source_id,first_context);
    PERFORM pg_temp.operation_cell(first_root,input_id,alternate_input,source_id,first_context);
    SELECT * INTO r FROM pg_temp.operation_receipt(first_prompt,2,p_context=>first_context,p_hops=>1);
    IF r.emitted IS DISTINCT FROM ARRAY[answer_id]
       OR r.complete IS DISTINCT FROM false OR r.remaining IS NULL OR r.remaining <= 0 THEN
        RAISE EXCEPTION 'FAIL: a shared surface discharged a canonical input with no actual result: %',r;
    END IF;

    PERFORM pg_temp.operation_cell(alternate_input,operation_id,alternate_answer,source_id,first_context);
    SELECT * INTO r FROM pg_temp.operation_receipt(first_prompt,1,p_context=>first_context,p_hops=>1);
    IF cardinality(r.emitted) IS DISTINCT FROM 1
       OR r.complete IS DISTINCT FROM false OR r.remaining IS NULL OR r.remaining <= 0 THEN
        RAISE EXCEPTION 'FAIL: one emitted result discharged both canonical inputs sharing a surface: %',r;
    END IF;
    SELECT * INTO r FROM pg_temp.operation_receipt(first_prompt,2,p_context=>first_context,p_hops=>1);
    IF cardinality(r.emitted) IS DISTINCT FROM 2
       OR NOT r.emitted @> ARRAY[answer_id,alternate_answer]
       OR r.complete IS DISTINCT FROM true OR r.remaining IS DISTINCT FROM 0 THEN
        RAISE EXCEPTION 'FAIL: both actual semantic input results did not complete the shared-surface call: %',r;
    END IF;
    RAISE NOTICE 'operational memory: canonical inputs sharing one surface retain separate completion obligations';

    -- Restore the single-input source contract before the competing-source
    -- control below; the current result cells remain available to both reads.
    DELETE FROM laplace.attestations
     WHERE context_id=first_context
       AND ((subject_id=first_root AND type_id=input_id AND object_id=alternate_input)
         OR (subject_id=first_surface AND type_id=senses_id AND object_id=alternate_input));
    DELETE FROM laplace.consensus
     WHERE (subject_id=first_root AND type_id=input_id AND object_id=alternate_input)
        OR (subject_id=first_surface AND type_id=senses_id AND object_id=alternate_input);

    -- One surface occurrence can address two distinct concepts. Matching their
    -- occurrence bitmaps does not make their canonical input identities equal.
    -- Both sources use the same active context and predicate, but attest
    -- different semantic inputs for the same complete observation.
    PERFORM pg_temp.operation_cell(first_surface,senses_id,alternate_input,alternate_source,first_context);
    PERFORM pg_temp.operation_cell(first_root,calls_id,operation_id,alternate_source,first_context);
    PERFORM pg_temp.operation_cell(first_root,input_id,alternate_input,alternate_source,first_context);
    PERFORM pg_temp.operation_cell(alternate_input,operation_id,alternate_answer,alternate_source,first_context);
    SELECT * INTO r FROM pg_temp.operation_receipt(first_prompt,1,p_context=>first_context,p_hops=>1);
    IF r.complete IS DISTINCT FROM false OR r.disposition IS DISTINCT FROM 'ambiguous' THEN
        RAISE EXCEPTION 'FAIL: equal surface occurrence origins collapsed distinct canonical input contracts: %',r;
    END IF;
    RAISE NOTICE 'operational memory: distinct semantic inputs remain ambiguous even when their surface occurrence is shared';
END
$multilingual_semantic_input$;

-- A complete typed parse supplies an occurrence-bound lemma. No free-standing
-- lexical edge connects the current input surface to this semantic input.
DO $structural_lemma_binding$
DECLARE
    prompt text := 'structcue surfaceplural';
    reordered text := 'surfaceplural structcue';
    wrapped text := '"structcue surfaceplural"';
    cue bytea := laplace.word_id('structcue');
    surface bytea := laplace.word_id('surfaceplural');
    lemma bytea := laplace.word_id('structurallemma');
    answer bytea := public.laplace_hash128_blake3('test/structure/answer');
    source bytea := public.laplace_hash128_blake3('test/structure/source');
    scope bytea := public.laplace_hash128_blake3('test/structure/active-context');
    occurrence bytea := public.laplace_hash128_blake3('test/structure/parse-occurrence');
    lang bytea := public.laplace_hash128_blake3('test/structure/language');
    schema bytea := public.laplace_hash128_blake3('ud/parse/schema/v1');
    none_id bytea := public.laplace_hash128_blake3('ud/parse/none/v1');
    root_marker bytea := public.laplace_hash128_blake3('ud/parse/root/v1');
    features_end bytea := public.laplace_hash128_blake3('ud/parse/features-end/v1');
    enhanced_end bytea := public.laplace_hash128_blake3('ud/parse/enhanced-end/v1');
    misc_end bytea := public.laplace_hash128_blake3('ud/parse/misc-end/v1');
    tokens_end bytea := public.laplace_hash128_blake3('ud/parse/tokens-end/v1');
    ref_one bytea := public.laplace_hash128_blake3('ud/token-ref/31/v1');
    ref_two bytea := public.laplace_hash128_blake3('ud/token-ref/32/v1');
    pos_id bytea := public.laplace_hash128_blake3('test/structure/pos');
    dep_id bytea := public.laplace_hash128_blake3('test/structure/dependency');
    calls bytea := laplace.relation_type_id('CALLS');
    inputs bytea := laplace.relation_type_id('HAS_INPUT');
    result_relation bytea := laplace.relation_type_id('CAUSES');
    has_parse bytea := laplace.relation_type_id('HAS_PARSE');
    current_root bytea;
    other_root bytea;
    parse_id bytea;
    manifest bytea[];
    metadata bytea;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT id,2,laplace.entity_type_id('Source_Reference'),source
      FROM unnest(ARRAY[source,scope,occurrence,lang,schema,none_id,root_marker,
          features_end,enhanced_end,misc_end,tokens_end,ref_one,ref_two,pos_id,dep_id,answer]) id
    ON CONFLICT (id,tier) DO NOTHING;
    PERFORM pg_temp.admit_operational_prompt(prompt,source);
    PERFORM pg_temp.admit_operational_prompt(reordered,source);
    PERFORM pg_temp.admit_operational_prompt(wrapped,source);
    PERFORM pg_temp.admit_operational_prompt('structurallemma',source);
    SELECT root_id INTO current_root FROM converse.prompt_tree(prompt) LIMIT 1;
    PERFORM pg_temp.operation_cell(current_root,calls,result_relation,source,scope);
    PERFORM pg_temp.operation_cell(current_root,inputs,lemma,source,scope);
    PERFORM pg_temp.operation_cell(lemma,result_relation,answer,source,scope);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,p_context=>scope,p_hops=>1);
    IF r.emitted IS NOT NULL OR r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: ungrounded lemma input executed before parse admission: %',r;
    END IF;

    manifest := ARRAY[schema,current_root,lang,
        ref_one,cue,cue,pos_id,none_id,features_end,root_marker,dep_id,enhanced_end,misc_end,
        ref_two,surface,lemma,pos_id,none_id,features_end,ref_one,dep_id,enhanced_end,misc_end,
        tokens_end];
    parse_id := public.laplace_hash128_merkle(4::smallint,manifest);
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    VALUES(parse_id,4,laplace.entity_type_id('UD_Parse'),source);
    INSERT INTO laplace.physicalities
        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,observed_at)
    VALUES(public.laplace_hash128_blake3(parse_id || decode('0800','hex')),
        parse_id,8,'SRID=0;POINT ZM(0 0 0 0)'::geometry,
        decode(repeat('00',16),'hex'),public.laplace_trajectory_build(manifest),
        cardinality(manifest),now());
    PERFORM pg_temp.operation_cell(current_root,has_parse,parse_id,source,occurrence);
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,p_context=>scope,p_hops=>1);
    IF r.emitted IS DISTINCT FROM ARRAY[answer] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: complete witnessed parse did not bind its input lemma: %',r;
    END IF;

    -- A selected semantic input must be routed even at the naming-hop boundary.
    -- Its declared predicate supplies the result projection; higher-ranked
    -- unrelated cells cannot hide that result in an unmasked fanout window.
    FOR i IN 1..3 LOOP
        metadata := public.laplace_hash128_blake3('test/structure/metadata/' || i::text);
        INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
        VALUES(metadata,2,laplace.entity_type_id('CodeConcept'),source);
        PERFORM pg_temp.operation_cell(lemma,laplace.relation_type_id('RELATED_TO'),metadata,source,scope);
    END LOOP;
    UPDATE laplace.consensus SET rating=3000000000000
     WHERE subject_id=lemma AND type_id=laplace.relation_type_id('RELATED_TO');
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,p_context=>scope,p_hops=>0,p_fanout=>2);
    IF r.emitted IS DISTINCT FROM ARRAY[answer] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: declared input/predicate route was hidden by hop limit or unrelated metadata: %',r;
    END IF;

    -- An annotation discovered through the same constituent set must still
    -- preserve full order. Additional quote marks are observations, not noise.
    SELECT root_id INTO other_root FROM converse.prompt_tree(reordered) LIMIT 1;
    PERFORM pg_temp.operation_cell(other_root,calls,result_relation,source,scope);
    PERFORM pg_temp.operation_cell(other_root,inputs,lemma,source,scope);
    SELECT * INTO r FROM pg_temp.operation_receipt(reordered,1,p_context=>scope,p_hops=>1);
    IF r.emitted IS NOT NULL OR r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: parse role binding ignored current word order: %',r;
    END IF;
    SELECT root_id INTO other_root FROM converse.prompt_tree(wrapped) LIMIT 1;
    PERFORM pg_temp.operation_cell(other_root,calls,result_relation,source,scope);
    PERFORM pg_temp.operation_cell(other_root,inputs,lemma,source,scope);
    SELECT * INTO r FROM pg_temp.operation_receipt(wrapped,1,p_context=>scope,p_hops=>1);
    IF r.emitted IS NOT NULL OR r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: parse role binding discarded quote observations: %',r;
    END IF;

    UPDATE laplace.attestations SET outcome=0
     WHERE subject_id=current_root AND type_id=has_parse AND object_id=parse_id
       AND source_id=source AND context_id=occurrence;
    SELECT * INTO r FROM pg_temp.operation_receipt(prompt,1,p_context=>scope,p_hops=>1);
    IF r.emitted IS NOT NULL OR r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: refuted source parse still contributed a lemma binding: %',r;
    END IF;
    RAISE NOTICE 'operational memory: native parse roles bind exact ordered occurrences; quoted, reordered and refuted annotations cannot supply inputs';
END
$structural_lemma_binding$;
ROLLBACK;
\set ECHO all
