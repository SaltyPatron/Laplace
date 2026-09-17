from pathlib import Path
import re

p = Path('extension/laplace_substrate/tests/generated_stage_sink_native_probe.c')
text = p.read_text()
# The old probe had a final refusal-path assertion that no lock query ran before
# validation. There is no lock query in the new enum, so remove the dead conjunct.
text = re.sub(r'query_calls\[SQ_LOCK\]\s*==\s*0\s*&&\s*', '', text)
text = text.replace('CHECK(query_calls[SQ_LOCK]==0);', '')
if 'SQ_LOCK' in text:
    lines = [line.strip() for line in text.splitlines() if 'SQ_LOCK' in line]
    raise SystemExit('unhandled SQ_LOCK references: ' + ' | '.join(lines))
p.write_text(text)
