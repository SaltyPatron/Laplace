-- Reusable task shapes bind new semantic IDs through complete witnessed parses.
-- Fixture setup supplies source records; ordinary native forward_program executes.
\set ECHO none
BEGIN;

CREATE FUNCTION pg_temp.shape_surface(p_text text,p_source bytea)
RETURNS bytea LANGUAGE plpgsql AS $surface$
DECLARE v_root bytea;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT DISTINCT p.id,p.tier,laplace.entity_type_id(CASE p.tier
        WHEN 0 THEN 'Codepoint' WHEN 1 THEN 'Grapheme' WHEN 2 THEN 'Word'
        WHEN 3 THEN 'Sentence' ELSE 'Document' END),p_source
      FROM converse.prompt_tree(p_text,false) p
    ON CONFLICT (id,tier) DO NOTHING;
    SELECT root_id INTO v_root FROM converse.prompt_tree(p_text) LIMIT 1;
    RETURN v_root;
END
$surface$;

CREATE FUNCTION pg_temp.shape_cell(
    p_subject bytea,p_type bytea,p_object bytea,p_source bytea,p_context bytea)
RETURNS void LANGUAGE plpgsql AS $cell$
BEGIN
    INSERT INTO laplace.attestations
        (id,subject_id,type_id,object_id,source_id,context_id,outcome,
         last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
    VALUES(public.laplace_hash128_blake3(
        p_subject || p_type || p_object || p_source || COALESCE(p_context,''::bytea)),
        p_subject,p_type,p_object,p_source,p_context,2,now(),5,5000000000,30000000000)
    ON CONFLICT (id,type_id,subject_id) DO UPDATE
       SET outcome=2,observation_count=5,sum_score_fp1e9=5000000000;
    INSERT INTO laplace.consensus
        (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
    VALUES(laplace.consensus_id(p_subject,p_type,p_object),p_subject,p_type,p_object,
        2000000000000,30000000000,60000000,5,now())
    ON CONFLICT (id,type_id,subject_id) DO UPDATE
       SET rating=2000000000000,rd=30000000000,witness_count=5;
END
$cell$;

-- Same tier, physicality identity and packed carrier as the source codec.
CREATE FUNCTION pg_temp.shape_projection(p_flat bytea[],p_type text,p_source bytea)
RETURNS bytea LANGUAGE plpgsql AS $projection$
DECLARE v_id bytea := public.laplace_hash128_merkle(4::smallint,p_flat);
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    VALUES(v_id,4,laplace.entity_type_id(p_type),p_source)
    ON CONFLICT (id,tier) DO NOTHING;
    INSERT INTO laplace.physicalities
        (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
    VALUES(public.laplace_hash128_blake3(v_id || decode('0800','hex')),
        v_id,8,'SRID=0;POINT ZM(0 0 0 0)'::geometry,decode(repeat('00',16),'hex'),
        public.laplace_trajectory_build(p_flat),cardinality(p_flat))
    ON CONFLICT (id) DO NOTHING;
    RETURN v_id;
END
$projection$;

CREATE FUNCTION pg_temp.shape_ref(p_ordinal int)
RETURNS bytea LANGUAGE sql IMMUTABLE AS $ref$
    SELECT public.laplace_hash128_blake3(convert_to(
        'ud/token-ref/' || upper(encode(convert_to(p_ordinal::text,'UTF8'),'hex')) || '/v1','UTF8'));
$ref$;

-- Authored UD annotations are serialized, never inferred by this fixture.
CREATE FUNCTION pg_temp.shape_parse(
    p_text text,p_forms text[],p_heads int[],p_dependencies bytea[],
    p_language bytea,p_source bytea,p_context bytea,
    p_first_features bytea[] DEFAULT ARRAY[]::bytea[])
RETURNS bytea LANGUAGE plpgsql AS $parse$
DECLARE
    v_root bytea := pg_temp.shape_surface(p_text,p_source);
    v_flat bytea[] := ARRAY[public.laplace_hash128_blake3('ud/parse/schema/v1'),v_root,p_language];
    v_none bytea := public.laplace_hash128_blake3('ud/parse/none/v1');
    v_form bytea;
    v_parse bytea;
BEGIN
    FOR i IN 1..cardinality(p_forms) LOOP
        v_form := pg_temp.shape_surface(p_forms[i],p_source);
        v_flat := v_flat || ARRAY[pg_temp.shape_ref(i),v_form,v_form,
            public.laplace_hash128_blake3('test/task-shapes/upos'),v_none];
        IF i=1 THEN v_flat := v_flat || p_first_features; END IF;
        v_flat := v_flat || ARRAY[
            public.laplace_hash128_blake3('ud/parse/features-end/v1'),
            CASE WHEN p_heads[i]=0 THEN public.laplace_hash128_blake3('ud/parse/root/v1')
                 ELSE pg_temp.shape_ref(p_heads[i]) END,p_dependencies[i],
            public.laplace_hash128_blake3('ud/parse/enhanced-end/v1'),
            public.laplace_hash128_blake3('ud/parse/misc-end/v1')];
    END LOOP;
    v_flat := v_flat || ARRAY[public.laplace_hash128_blake3('ud/parse/tokens-end/v1')];
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT DISTINCT value,2,laplace.entity_type_id('UD_Annotation_Marker'),p_source
      FROM unnest(v_flat) value
     WHERE NOT EXISTS (SELECT 1 FROM laplace.entities e WHERE e.id=value)
    ON CONFLICT (id,tier) DO NOTHING;
    v_parse := pg_temp.shape_projection(v_flat,'UD_Parse',p_source);
    PERFORM pg_temp.shape_cell(v_root,laplace.relation_type_id('HAS_PARSE'),v_parse,p_source,p_context);
    RETURN v_parse;
END
$parse$;

CREATE FUNCTION pg_temp.declare_shape(
    p_exemplar bytea,p_predicate bytea,p_ordinals int[],p_types bytea[],p_source bytea,p_context bytea)
RETURNS bytea LANGUAGE plpgsql AS $shape$
DECLARE
    v_slot_schema bytea := public.laplace_hash128_blake3('laplace/task-shape/token-slot/v1');
    v_flat bytea[] := ARRAY[public.laplace_hash128_blake3(
        'laplace/task-shape/relation-read/token-slots/v1'),p_exemplar,p_predicate];
    v_slots bytea[] := ARRAY[]::bytea[];
    v_slot bytea;
    v_shape bytea;
BEGIN
    FOR i IN 1..cardinality(p_ordinals) LOOP
        v_slot := public.laplace_hash128_merkle(4::smallint,
            ARRAY[v_slot_schema,pg_temp.shape_ref(p_ordinals[i]),p_types[i]]);
        v_slots := v_slots || v_slot;
        v_flat := v_flat || ARRAY[v_slot,pg_temp.shape_ref(p_ordinals[i]),p_types[i]];
        INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
        VALUES(v_slot,4,laplace.entity_type_id('CodeConcept'),p_source)
        ON CONFLICT (id,tier) DO NOTHING;
    END LOOP;
    v_flat := v_flat || ARRAY[public.laplace_hash128_blake3('laplace/task-shape/slots-end/v1')];
    v_shape := pg_temp.shape_projection(v_flat,'CodeConcept',p_source);
    PERFORM pg_temp.shape_cell(p_exemplar,laplace.relation_type_id('IS_EXAMPLE_OF'),v_shape,p_source,p_context);
    PERFORM pg_temp.shape_cell(v_shape,laplace.relation_type_id('CALLS'),p_predicate,p_source,p_context);
    FOREACH v_slot IN ARRAY v_slots LOOP
        PERFORM pg_temp.shape_cell(v_shape,laplace.relation_type_id('HAS_INPUT'),v_slot,p_source,p_context);
    END LOOP;
    RETURN v_shape;
END
$shape$;

CREATE FUNCTION pg_temp.shape_receipt(p_prompt text,p_steps int DEFAULT 2,p_fanout int DEFAULT 256)
RETURNS TABLE(emitted bytea[],complete boolean,disposition text,program_id bytea,remaining int)
LANGUAGE sql AS $receipt$
    SELECT array_agg(p.entity ORDER BY p.step) FILTER (WHERE p.event='emit'),
        bool_or(p.completion),
        (array_agg(p.disposition ORDER BY p.step DESC)
            FILTER (WHERE p.event IN ('complete','unresolved')))[1],
        (array_agg(p.program_id ORDER BY p.step DESC))[1],
        (array_agg(p.remaining_required ORDER BY p.step DESC)
            FILTER (WHERE p.event IN ('complete','unresolved')))[1]
      FROM generation.forward_program(p_prompt,p_steps,0,0.0,16,7,2,p_fanout,NULL,NULL) p;
$receipt$;

CREATE FUNCTION pg_temp.shape_reject(p_label text,p_prompt text,p_fanout int DEFAULT 256)
RETURNS void LANGUAGE plpgsql AS $reject$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_temp.shape_receipt(p_prompt,p_fanout=>p_fanout);
    IF r.emitted IS NOT NULL OR r.complete IS DISTINCT FROM false THEN
        RAISE EXCEPTION 'FAIL: % executed without a complete applicable shape: %',p_label,r;
    END IF;
END
$reject$;

DO $novel_request$
DECLARE
    v_source bytea := public.laplace_hash128_blake3('test/task-shapes/source');
    v_context bytea := public.laplace_hash128_blake3('test/task-shapes/source-file');
    v_other_context bytea := public.laplace_hash128_blake3('test/task-shapes/other-file');
    v_other_source bytea := public.laplace_hash128_blake3('test/task-shapes/other-source');
    v_language bytea := public.laplace_hash128_blake3('test/task-shapes/language');
    v_input bytea := public.laplace_hash128_blake3('test/task-shapes/new-semantic-input');
    v_answer bytea := public.laplace_hash128_blake3('test/task-shapes/answer');
    v_changed bytea := public.laplace_hash128_blake3('test/task-shapes/changed-answer');
    v_other_answer bytea := public.laplace_hash128_blake3('test/task-shapes/other-predicate-answer');
    v_other_input bytea := public.laplace_hash128_blake3('test/task-shapes/other-meaning');
    v_concept bytea := laplace.entity_type_id('CodeConcept');
    v_predicate bytea := laplace.relation_type_id('CAUSES');
    v_other_predicate bytea := laplace.relation_type_id('HAS_DEFINITION');
    v_calls bytea := laplace.relation_type_id('CALLS');
    v_inputs bytea := laplace.relation_type_id('HAS_INPUT');
    v_example_of bytea := laplace.relation_type_id('IS_EXAMPLE_OF');
    v_has_parse bytea := laplace.relation_type_id('HAS_PARSE');
    v_deps bytea[] := ARRAY[public.laplace_hash128_blake3('test/task-shapes/root-role'),
        public.laplace_hash128_blake3('test/task-shapes/input-role')];
    v_prompt text := 'ζαλκ ñébulo';
    v_exemplar bytea;
    v_current bytea;
    v_root bytea;
    v_surface bytea;
    v_shape bytea;
    v_other_shape bytea;
    v_slot bytea;
    v_alternative bytea;
    v_program bytea;
    v_inferred_program bytea;
    v_cell record;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT value,2,v_concept,v_source FROM unnest(ARRAY[v_source,v_context,v_other_context,v_other_source,
        v_language,v_input,v_answer,v_changed,v_other_answer,v_other_input]) value;
    v_exemplar := pg_temp.shape_parse('ζαλκ próbulo',ARRAY['ζαλκ','próbulo'],ARRAY[0,1],
        v_deps,v_language,v_source,v_context);
    v_root := pg_temp.shape_surface(v_prompt,v_source);
    v_surface := pg_temp.shape_surface('ñébulo',v_source);
    PERFORM pg_temp.shape_cell(v_surface,laplace.relation_type_id('HAS_SENSE'),v_input,v_source,v_context);
    PERFORM pg_temp.shape_cell(v_input,v_predicate,v_answer,v_source,v_context);
    PERFORM pg_temp.shape_reject('available result without task shape',v_prompt);
    v_shape := pg_temp.declare_shape(v_exemplar,v_predicate,ARRAY[2],ARRAY[v_concept],v_source,v_context);
    v_slot := public.laplace_hash128_merkle(4::smallint,ARRAY[
        public.laplace_hash128_blake3('laplace/task-shape/token-slot/v1'),pg_temp.shape_ref(2),v_concept]);
    IF EXISTS (SELECT 1 FROM laplace.attestations
                WHERE subject_id=v_root AND type_id IN (v_calls,v_inputs,v_has_parse))
       OR EXISTS (SELECT 1 FROM generation.trajectory_unpacked_points(v_exemplar,8::smallint) p
                   WHERE p.entity_id=v_input) THEN
        RAISE EXCEPTION 'FAIL: novel input/request was pre-bound by the fixture';
    END IF;
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    IF r.emitted IS DISTINCT FROM ARRAY[v_answer] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: ordinary forward program did not apply a witnessed shape to a new root/input: %',r;
    END IF;
    v_program := r.program_id;
    IF EXISTS (SELECT 1 FROM laplace.attestations WHERE subject_id=v_root AND type_id=v_has_parse) THEN
        RAISE EXCEPTION 'FAIL: derived request structure was fabricated as a witnessed HAS_PARSE observation';
    END IF;

    DELETE FROM laplace.consensus WHERE subject_id=v_input AND type_id=v_predicate AND object_id=v_answer;
    PERFORM pg_temp.shape_cell(v_input,v_predicate,v_changed,v_source,v_context);
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    IF r.emitted IS DISTINCT FROM ARRAY[v_changed] OR r.complete IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'FAIL: shape replayed an exemplar answer instead of reading changed substrate state: %',r;
    END IF;
    RAISE NOTICE 'task shapes: ordinary native forward program binds a new Unicode request and semantic input; result changes alter output';

    -- Each declared applicability/call/slot record is necessary, independently
    -- of positive pooled consensus or another source record from the lesson.
    FOR v_cell IN SELECT * FROM (VALUES
        (v_exemplar,v_example_of,v_shape),(v_shape,v_calls,v_predicate),(v_shape,v_inputs,v_slot))
        AS cells(subject_id,type_id,object_id)
    LOOP
        UPDATE laplace.attestations SET outcome=0,sum_score_fp1e9=0
         WHERE subject_id=v_cell.subject_id AND type_id=v_cell.type_id AND object_id=v_cell.object_id;
        PERFORM pg_temp.shape_reject('refuted shape declaration',v_prompt);
        DELETE FROM laplace.attestations
         WHERE subject_id=v_cell.subject_id AND type_id=v_cell.type_id AND object_id=v_cell.object_id;
        PERFORM pg_temp.shape_reject('absent shape declaration with retained standing',v_prompt);
        PERFORM pg_temp.shape_cell(v_cell.subject_id,v_cell.type_id,v_cell.object_id,v_source,v_context);
    END LOOP;
    DELETE FROM laplace.attestations WHERE subject_id=v_shape AND type_id=v_inputs;
    PERFORM pg_temp.shape_cell(v_shape,v_inputs,v_slot,v_other_source,v_context);
    PERFORM pg_temp.shape_reject('slot from another source',v_prompt);
    DELETE FROM laplace.attestations WHERE subject_id=v_shape AND type_id=v_inputs;
    PERFORM pg_temp.shape_cell(v_shape,v_inputs,v_slot,v_source,v_other_context);
    PERFORM pg_temp.shape_reject('slot from another source-file context',v_prompt);
    DELETE FROM laplace.attestations WHERE subject_id=v_shape AND type_id=v_inputs;
    PERFORM pg_temp.shape_cell(v_shape,v_inputs,v_slot,v_source,NULL);
    PERFORM pg_temp.shape_reject('slot without source-file context',v_prompt);
    DELETE FROM laplace.attestations WHERE subject_id=v_shape AND type_id=v_inputs;
    PERFORM pg_temp.shape_cell(v_shape,v_inputs,v_slot,v_source,v_context);
    UPDATE laplace.consensus SET rating=1000000000000
     WHERE subject_id=v_shape AND type_id=v_inputs AND object_id=v_slot;
    PERFORM pg_temp.shape_reject('slot with negative pooled standing',v_prompt);
    PERFORM pg_temp.shape_cell(v_shape,v_inputs,v_slot,v_source,v_context);
    UPDATE laplace.entities SET type_id=laplace.entity_type_id('Word') WHERE id=v_input;
    PERFORM pg_temp.shape_reject('semantic input with incompatible declared type',v_prompt);
    UPDATE laplace.entities SET type_id=v_concept WHERE id=v_input;
    RAISE NOTICE 'task shapes: complete source and context testimony plus declared semantic input types are required';

    PERFORM pg_temp.shape_surface('«ζαλκ ñébulo»',v_source);
    PERFORM pg_temp.shape_reject('quoted observation is not the demonstrated request','«ζαλκ ñébulo»');
    -- Independently witnessed current annotations constrain the projected
    -- structure. A source-observed incompatible parse cannot be overwritten.
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    v_inferred_program := r.program_id;
    v_current := pg_temp.shape_parse(v_prompt,ARRAY['ζαλκ','ñébulo'],ARRAY[0,1],
        v_deps,v_language,v_source,v_context);
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    IF r.emitted IS DISTINCT FROM ARRAY[v_changed] OR r.complete IS DISTINCT FROM true
       OR r.program_id IS NULL OR v_inferred_program IS NULL OR r.program_id=v_inferred_program THEN
        RAISE EXCEPTION 'FAIL: compatible witnessed current parse did not preserve shape execution: %',r;
    END IF;
    UPDATE laplace.attestations SET outcome=0
     WHERE subject_id=v_root AND type_id=v_has_parse AND object_id=v_current;
    v_alternative := pg_temp.shape_parse(v_prompt,ARRAY['ζαλκ','ñébulo'],ARRAY[0,1],
        v_deps,v_language,v_source,v_context,ARRAY[
            public.laplace_hash128_blake3('test/task-shapes/polarity-feature'),
            public.laplace_hash128_blake3('test/task-shapes/negative-value')]);
    PERFORM pg_temp.shape_reject('changed polarity annotation',v_prompt);
    DELETE FROM laplace.attestations WHERE type_id=v_has_parse AND object_id=v_alternative;
    v_alternative := pg_temp.shape_parse(v_prompt,ARRAY['ζαλκ','ñébulo'],ARRAY[2,0],
        ARRAY[v_deps[2],v_deps[1]],v_language,v_source,v_context);
    PERFORM pg_temp.shape_reject('changed dependency roles',v_prompt);
    DELETE FROM laplace.attestations WHERE type_id=v_has_parse AND object_id=v_alternative;
    PERFORM pg_temp.shape_cell(v_root,v_has_parse,v_current,v_source,v_context);
    PERFORM pg_temp.shape_reject('fanout cannot retain and execute a partial shape',v_prompt,1);
    RAISE NOTICE 'task shapes: quote boundaries, polarity, dependency roles and incomplete fanout cannot be discarded';

    PERFORM pg_temp.shape_cell(v_surface,laplace.relation_type_id('HAS_SENSE'),v_other_input,v_source,v_context);
    PERFORM pg_temp.shape_cell(v_other_input,v_predicate,v_other_answer,v_source,v_context);
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    IF r.complete IS DISTINCT FROM false OR r.disposition IS DISTINCT FROM 'ambiguous' THEN
        RAISE EXCEPTION 'FAIL: distinct meanings sharing one surface slot lost their ambiguity: %',r;
    END IF;
    DELETE FROM laplace.attestations
     WHERE subject_id=v_surface AND type_id=laplace.relation_type_id('HAS_SENSE') AND object_id=v_other_input;
    DELETE FROM laplace.consensus
     WHERE subject_id=v_surface AND type_id=laplace.relation_type_id('HAS_SENSE') AND object_id=v_other_input;

    -- A different declared predicate means a different canonical shape. No
    -- compiled lexical dispatch chooses either predicate for the Unicode cue.
    PERFORM pg_temp.shape_cell(v_input,v_other_predicate,v_other_answer,v_source,v_context);
    v_other_shape := pg_temp.declare_shape(v_exemplar,v_other_predicate,ARRAY[2],ARRAY[v_concept],v_source,v_context);
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    IF r.complete IS DISTINCT FROM false OR r.disposition IS DISTINCT FROM 'ambiguous' THEN
        RAISE EXCEPTION 'FAIL: alternative task predicates were silently selected: %',r;
    END IF;
    DELETE FROM laplace.attestations
     WHERE subject_id=v_exemplar AND type_id=v_example_of AND object_id=v_shape;
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt);
    IF r.emitted IS DISTINCT FROM ARRAY[v_other_answer] OR r.complete IS DISTINCT FROM true
       OR r.program_id IS NULL OR v_program IS NULL OR r.program_id=v_program THEN
        RAISE EXCEPTION 'FAIL: data-declared predicate change did not change actual query/program identity: %',r;
    END IF;
    RAISE NOTICE 'task shapes: alternative predicates stay ambiguous; changing the declared predicate changes the live query and program identity';
END
$novel_request$;

DO $separate_slots$
DECLARE
    v_source bytea := public.laplace_hash128_blake3('test/task-shapes/pair-source');
    v_context bytea := public.laplace_hash128_blake3('test/task-shapes/pair-context');
    v_language bytea := public.laplace_hash128_blake3('test/task-shapes/pair-language');
    v_inputs bytea[] := ARRAY[public.laplace_hash128_blake3('test/task-shapes/input-a'),
        public.laplace_hash128_blake3('test/task-shapes/input-b')];
    v_answers bytea[] := ARRAY[public.laplace_hash128_blake3('test/task-shapes/answer-a'),
        public.laplace_hash128_blake3('test/task-shapes/answer-b')];
    v_concept bytea := laplace.entity_type_id('CodeConcept');
    v_predicate bytea := laplace.relation_type_id('CAUSES');
    v_deps bytea[] := ARRAY[public.laplace_hash128_blake3('test/task-shapes/pair-root'),
        public.laplace_hash128_blake3('test/task-shapes/pair-first-role'),
        public.laplace_hash128_blake3('test/task-shapes/pair-second-role')];
    v_prompt text := 'ψόλκ bíreno dúreno';
    v_exemplar bytea;
    v_surface bytea;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT value,2,v_concept,v_source FROM unnest(ARRAY[v_source,v_context,v_language] || v_inputs || v_answers) value;
    v_exemplar := pg_temp.shape_parse('ψόλκ cáreno féreno',ARRAY['ψόλκ','cáreno','féreno'],
        ARRAY[0,1,1],v_deps,v_language,v_source,v_context);
    PERFORM pg_temp.shape_surface(v_prompt,v_source);
    PERFORM pg_temp.declare_shape(v_exemplar,v_predicate,ARRAY[2,3],ARRAY[v_concept,v_concept],v_source,v_context);
    v_surface := pg_temp.shape_surface('bíreno',v_source);
    PERFORM pg_temp.shape_cell(v_surface,laplace.relation_type_id('HAS_SENSE'),v_inputs[1],v_source,v_context);
    v_surface := pg_temp.shape_surface('dúreno',v_source);
    PERFORM pg_temp.shape_cell(v_surface,laplace.relation_type_id('HAS_SENSE'),v_inputs[2],v_source,v_context);
    PERFORM pg_temp.shape_cell(v_inputs[1],v_predicate,v_answers[1],v_source,v_context);
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt,3);
    IF r.complete IS DISTINCT FROM false OR COALESCE(r.remaining,0)<1 THEN
        RAISE EXCEPTION 'FAIL: one available result discharged two different declared semantic slots: %',r;
    END IF;
    PERFORM pg_temp.shape_cell(v_inputs[2],v_predicate,v_answers[2],v_source,v_context);
    SELECT * INTO r FROM pg_temp.shape_receipt(v_prompt,3);
    IF r.complete IS DISTINCT FROM true OR cardinality(r.emitted) IS DISTINCT FROM 2
       OR NOT (r.emitted @> v_answers AND r.emitted <@ v_answers) THEN
        RAISE EXCEPTION 'FAIL: complete declared slots did not execute each live semantic input: %',r;
    END IF;
    RAISE NOTICE 'task shapes: separate declared slots retain separate semantic completion obligations';
END
$separate_slots$;

DO $language_surfaces$
DECLARE
    v_source bytea := public.laplace_hash128_blake3('test/task-shapes/languages-source');
    v_context bytea := public.laplace_hash128_blake3('test/task-shapes/languages-context');
    v_input bytea := public.laplace_hash128_blake3('test/task-shapes/shared-concept');
    v_answer bytea := public.laplace_hash128_blake3('test/task-shapes/shared-result');
    v_concept bytea := laplace.entity_type_id('CodeConcept');
    v_predicate bytea := laplace.relation_type_id('CAUSES');
    v_deps bytea[] := ARRAY[public.laplace_hash128_blake3('test/task-shapes/languages-root'),
        public.laplace_hash128_blake3('test/task-shapes/languages-input')];
    v_exemplar bytea;
    v_surface bytea;
    v_language bytea;
    v_cue text;
    v_word text;
    v_example_word text;
    r record;
BEGIN
    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
    SELECT value,2,v_concept,v_source FROM unnest(ARRAY[v_source,v_context,v_input,v_answer]) value;
    PERFORM pg_temp.shape_cell(v_input,v_predicate,v_answer,v_source,v_context);
    FOR i IN 1..2 LOOP
        v_language := public.laplace_hash128_blake3(convert_to('test/task-shapes/language-' || i,'UTF8'));
        v_cue := CASE i WHEN 1 THEN 'χέλκ' ELSE '觅' END;
        v_word := CASE i WHEN 1 THEN 'eau' ELSE '水' END;
        v_example_word := CASE i WHEN 1 THEN 'brume' ELSE '火' END;
        v_exemplar := pg_temp.shape_parse(v_cue || ' ' || v_example_word,
            ARRAY[v_cue,v_example_word],ARRAY[0,1],v_deps,v_language,v_source,v_context);
        PERFORM pg_temp.shape_surface(v_cue || ' ' || v_word,v_source);
        PERFORM pg_temp.declare_shape(v_exemplar,v_predicate,ARRAY[2],ARRAY[v_concept],v_source,v_context);
        v_surface := pg_temp.shape_surface(v_word,v_source);
        PERFORM pg_temp.shape_cell(v_surface,laplace.relation_type_id('HAS_SENSE'),v_input,v_source,v_context);
        SELECT * INTO r FROM pg_temp.shape_receipt(v_cue || ' ' || v_word);
        IF r.emitted IS DISTINCT FROM ARRAY[v_answer] OR r.complete IS DISTINCT FROM true THEN
            RAISE EXCEPTION 'FAIL: language-specific surface structure changed the shared semantic input/result: %',r;
        END IF;
    END LOOP;
    RAISE NOTICE 'task shapes: independently witnessed language structures converge on shared semantic input and predicate identities';
END
$language_surfaces$;

ROLLBACK;
\set ECHO all
