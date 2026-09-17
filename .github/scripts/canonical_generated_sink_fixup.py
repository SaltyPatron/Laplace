from pathlib import Path
import re

p = Path('extension/laplace_substrate/tests/generated_stage_sink_native_probe.c')
text = p.read_text()
# The old probe had refusal-path assertions that no lock query ran before
# validation. There is no lock query in the new enum, so remove the dead
# conjunct whether it appears before or after the remaining assertion.
text = re.sub(r'query_calls\[SQ_LOCK\]\s*==\s*0\s*&&\s*', '', text)
text = re.sub(r'\s*&&\s*query_calls\[SQ_LOCK\]\s*==\s*0', '', text)
text = text.replace('CHECK(query_calls[SQ_LOCK]==0);', '')
if 'SQ_LOCK' in text:
    lines = [line.strip() for line in text.splitlines() if 'SQ_LOCK' in line]
    raise SystemExit('unhandled SQ_LOCK references: ' + ' | '.join(lines))
# A removed trailing conjunct can leave a whitespace-only line. Keep the
# generated patch git-diff-clean before the workflow commits it.
had_final_newline = text.endswith('\n')
text = '\n'.join(line.rstrip() for line in text.splitlines())
if had_final_newline:
    text += '\n'
p.write_text(text)
