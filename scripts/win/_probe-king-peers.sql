\set ON_ERROR_STOP on
SELECT laplace.word_id('King') = laplace.word_id('king') AS same_id;
SELECT laplace.word_id('king') = ANY(laplace.lexical_peers(laplace.word_id('King'))) AS king_in_peers;
SELECT laplace.word_id('King') = ANY(laplace.lexical_peers(laplace.word_id('king'))) AS cap_in_peers;
SELECT count(*) AS define_king_cap FROM laplace.define(laplace.word_id('King'), 5);
SELECT count(*) AS define_king_low FROM laplace.define(laplace.word_id('king'), 5);
SELECT reply FROM laplace.recall('define King') LIMIT 3;
