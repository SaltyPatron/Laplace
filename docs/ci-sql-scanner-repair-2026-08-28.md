# Merged-main integration failure: SQL scanner false positive

Run [33140056131](https://github.com/SaltyPatron/Laplace/actions/runs/33140056131)
at `c490b806` failed `ReadPath_NoNewHandWrittenSql`, naming `ChessLabPaths.cs`.
All 570 chess integration tests and the PostgreSQL regression lane passed.
Publication was skipped; the always-run API restoration succeeded.

The SQL scanner applied an unbounded verbatim-string regex to whole C# files.
It started at the existing verbatim Windows path, crossed its closing quote,
and matched `select distro` in the new Stockfish discovery comment as SQL.
The failure is reproduced locally against merged main, without database writes.

The scanner now uses the test-only pinned Microsoft C# parser to distinguish
literal contents, executable command references, and comments. Ordinary-string
leading SELECT and verbatim/raw-string embedded SELECT policies are retained.
Interpolations, escaped characters and inactive platform branches are covered.
The existing scan scope, allowlist entries and ratchet ceilings are unchanged.
The Stockfish implementation/comment was not rewritten to evade the detector.

Regression tests include the original path/comment counterexample, quoted text,
line/block comments, ordinary/verbatim/raw/interpolated SQL, UTF-8 literals,
escaped SELECT, command construction, and SQL added after an innocent comment.
The original repository gate failed before this repair; the repaired gate and
scanner suite pass 38 tests locally. CI now executes both suites before install,
while retaining the existing full integration gate.

This change only affects tests and CI. It does not activate MCP/Lichess, upgrade
Stockfish, change production chess decisions, or alter the separate semantic
election evaluation and its acceptance criteria.
