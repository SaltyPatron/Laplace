cmd /c ".\scripts\win\build-cutechess.cmd" > D:\Data\Output\build-cutechess.log
cmd /c ".\scripts\win\build-engine-asan.cmd" > D:\Data\Output\build-engine-asan.log
cmd /c ".\scripts\win\build-engine-libs.cmd" > D:\Data\Output\build-engine-libs.log
cmd /c ".\scripts\win\build-engine.cmd" > D:\Data\Output\build-engine.log
cmd /c ".\scripts\win\build-extensions.cmd" > D:\Data\Output\build-extensions.log
cmd /c ".\scripts\win\build-web.cmd" > D:\Data\Output\build-web.log
cmd /c ".\scripts\win\rebuild-all.cmd" > D:\Data\Output\rebuild-all.log
cmd /c ".\scripts\win\deploy-api.cmd" > D:\Data\Output\deploy-api.log
cmd /c ".\scripts\win\publish.cmd" > D:\Data\Output\publish.log

cmd /c ".\scripts\win\db-reset.cmd" > D:\Data\Output\db-reset.log
cmd /c ".\scripts\win\seed-foundation.cmd" > D:\Data\Output\seed-foundation.log
cmd /c ".\scripts\win\seed-step.cmd documents D:\Data\Ingest\test-data\text" > D:\Data\Output\documents.log
cmd /c ".\scripts\win\seed-step.cmd openings D:\Data\Ingest\Games\Chess\openings" > D:\Data\Output\openings.log
cmd /c ".\scripts\win\seed-step.cmd omw" > D:\Data\Output\omw.log
cmd /c ".\scripts\win\seed-step.cmd ud" > D:\Data\Output\ud.log
cmd /c ".\scripts\win\seed-step.cmd atomic2020" > D:\Data\Output\atomic2020.log
cmd /c ".\scripts\win\seed-step.cmd conceptnet" > D:\Data\Output\conceptnet.log
cmd /c ".\scripts\win\seed-step.cmd wiktionary" > D:\Data\Output\wiktionary.log
cmd /c ".\scripts\win\seed-step.cmd tatoeba" > D:\Data\Output\tatoeba.log
cmd /c ".\scripts\win\seed-step.cmd opensubtitles" > D:\Data\Output\opensubtitles.log
cmd /c ".\scripts\win\seed-step.cmd chess D:\Data\Ingest\Games\Chess\Lumbras\otb" > D:\Data\Output\.log
