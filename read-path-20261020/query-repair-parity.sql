\set ON_ERROR_STOP on
SELECT row_to_json(q) FROM (SELECT realize.render_batch(a) rendered,realize.label_batch(a) labels,converse.label_or_hex_batch(a) fallback FROM (SELECT ARRAY[laplace.relation_type_id('IS_A'),decode('aee37bba88f3c36bb240adab0a7444ee','hex'),laplace.word_id('a'),NULL::bytea,decode('b7b840fb843e7b1098cd32a101a2978d','hex'),laplace.relation_type_id('IS_A')] AS a) s) q;
