








\pset pager off
\timing on
SET laplace_substrate.perfcache_path = 'D:/Data/Postgres/laplace/share/laplace_t0_perfcache.bin';

\echo
\echo ===== SUBSTRATE STATE (plain text folded to consensus) =====
SELECT * FROM laplace.consensus_stats_approx();

\echo
\echo ===== relation types carried in consensus (PRECEDES = the book bigrams) =====
SELECT laplace.label(type_id) AS relation, count(*) AS relations
FROM laplace.consensus GROUP BY type_id ORDER BY 2 DESC LIMIT 8;

\echo
\echo ===== Q1  WHO ARE THE CAPTAINS?  words attested to follow the title "Captain" =====
\echo '       (Ahab, Peleg, Bildad, Sleet, Mayhew, Pollard, Boomer, Scoresby = real captains)'
SELECT laplace.label(c.object_id) AS captain,
       laplace.eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count AS games
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('Captain')
  AND c.type_id    = laplace.relation_type_id('PRECEDES')
  AND c.object_id IS NOT NULL
ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT 12;

\echo
\echo ===== Q1b CONTRAST: the common noun "captain" precedes function words, not names =====
\echo '       (same substrate separates the proper-noun title from the common noun, from text alone)'
SELECT laplace.label(c.object_id) AS after_captain, c.witness_count AS games
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('captain')
  AND c.type_id    = laplace.relation_type_id('PRECEDES')
  AND c.object_id IS NOT NULL
ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT 6;

\echo
\echo ===== Q2  WHAT TYPES OF WHALE?  words attested to precede "whale" =====
\echo '       (sperm, white, right, Greenland, great = the whale types in the novel)'
SELECT laplace.label(c.subject_id) AS type_of_whale,
       laplace.eff_mu_display(c.rating, c.rd) AS eff_mu, c.witness_count AS games
FROM laplace.consensus c
WHERE c.object_id = laplace.word_id('whale')
  AND c.type_id   = laplace.relation_type_id('PRECEDES')
ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT 12;

\echo
\echo ===== Q3  ICONIC PHRASE COMPLETION  "Moby" -> ? =====
SELECT laplace.label(c.object_id) AS after_moby, c.witness_count AS games
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('Moby')
  AND c.type_id    = laplace.relation_type_id('PRECEDES')
  AND c.object_id IS NOT NULL
ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT 3;

\echo
\echo ===== Q4  CROSS-BOOK (proves it is not Moby-specific)  "Sherlock" -> ? =====
SELECT laplace.label(c.object_id) AS after_sherlock, c.witness_count AS games
FROM laplace.consensus c
WHERE c.subject_id = laplace.word_id('Sherlock')
  AND c.type_id    = laplace.relation_type_id('PRECEDES')
  AND c.object_id IS NOT NULL
ORDER BY laplace.eff_mu(c.rating, c.rd) DESC LIMIT 3;
