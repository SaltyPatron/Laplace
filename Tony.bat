cmd /c ".\scripts\win\build-cutechess.cmd" 2>&1 > D:\Data\Output\build-cutechess.log
cmd /c ".\scripts\win\build-engine-asan.cmd" 2>&1 > D:\Data\Output\build-engine-asan.log
cmd /c ".\scripts\win\build-engine-libs.cmd" 2>&1 > D:\Data\Output\build-engine-libs.log
cmd /c ".\scripts\win\build-engine.cmd" 2>&1 > D:\Data\Output\build-engine.log
cmd /c ".\scripts\win\build-extensions.cmd" 2>&1 > D:\Data\Output\build-extensions.log
cmd /c ".\scripts\win\rebuild-all.cmd" 2>&1 > D:\Data\Output\rebuild-all.log
cmd /c ".\scripts\win\build-web.cmd" 2>&1 > D:\Data\Output\build-web.log
cmd /c ".\scripts\win\install-extensions.cmd" 2>&1 > D:\Data\Output\install-extensions.log
cmd /c ".\scripts\win\deploy-api.cmd" 2>&1 > D:\Data\Output\deploy-api.log
cmd /c ".\scripts\win\publish.cmd" 2>&1 > D:\Data\Output\publish.log
cmd /c ".\scripts\win\db-reset.cmd" 2>&1 > D:\Data\Output\db-reset.log
cmd /c ".\scripts\win\seed-foundation.cmd" 2>&1 > D:\Data\Output\seed-foundation.log
cmd /c ".\scripts\win\seed-step.cmd document D:\Data\Ingest\test-data\text" 2>&1 > D:\Data\Output\documents.log
cmd /c ".\scripts\win\seed-step.cmd openings D:\Data\Ingest\Games\Chess\openings" 2>&1 > D:\Data\Output\openings.log
cmd /c ".\scripts\win\seed-step.cmd atomic2020" 2>&1 > D:\Data\Output\atomic2020.log
cmd /c ".\scripts\win\seed-step.cmd omw" 2>&1 > D:\Data\Output\omw.log
cmd /c ".\scripts\win\seed-step.cmd conceptnet" 2>&1 > D:\Data\Output\conceptnet.log
cmd /c ".\scripts\win\seed-step.cmd ud" 2>&1 > D:\Data\Output\ud.log
cmd /c ".\scripts\win\seed-step.cmd wiktionary" 2>&1 > D:\Data\Output\wiktionary.log
cmd /c ".\scripts\win\seed-step.cmd tatoeba" 2>&1 > D:\Data\Output\tatoeba.log
cmd /c ".\scripts\win\seed-step.cmd opensubtitles" 2>&1 > D:\Data\Output\opensubtitles.log
cmd /c ".\scripts\win\seed-step.cmd chess D:\Data\Ingest\Games\Chess\Lumbras\otb" 2>&1 > D:\Data\Output\chess-otb.log
