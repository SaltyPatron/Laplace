cd /d/Repositories/Laplace
echo "[reset] dropping + recreating laplace"
cmd //c "scripts\win\db-reset.cmd" > .ingest-proof/cl-reset.log 2>&1
echo "[floor] unicode"; scripts/win/cli.cmd ingest unicode > .ingest-proof/cl-uni.out 2> .ingest-proof/cl-uni.err
echo "[floor] iso639";  scripts/win/cli.cmd ingest iso639  > .ingest-proof/cl-iso.out 2> .ingest-proof/cl-iso.err
echo "[ud] full 686";   scripts/win/cli.cmd ingest ud      > .ingest-proof/cl-ud.out  2> .ingest-proof/cl-ud.err
echo "[done]"
