"""Prove a changed evidence lock stops generation before existing views are replaced."""
from pathlib import Path
import json
import subprocess
import sys
import tempfile
from atlas_paths import REPOSITORIES, TOOLS, JAVA, PLANTUML

root=Path(__file__).resolve().parent
lock=json.loads((root/'reviewed-source-lock.json').read_text(encoding='utf-8'))
path=next(iter(lock))
with tempfile.TemporaryDirectory(prefix='source-lock-check-',dir=TOOLS) as work:
    target=Path(work)
    (target/'build_traces.py').write_bytes((root/'build_traces.py').read_bytes())
    (target/'atlas_paths.py').write_text('from pathlib import Path\n'+ '\n'.join(
        name+'=Path('+repr(str(value))+')' for name,value in [('REPOSITORIES',REPOSITORIES),('TOOLS',TOOLS),('PLANTUML',PLANTUML)])+'\nJAVA='+repr(JAVA),encoding='utf-8')
    (target/'reviewed-source-lock.json').write_text(json.dumps({path:'0'*64}),encoding='utf-8')
    (target/'traces.json').write_text('preserved',encoding='utf-8')
    result=subprocess.run([sys.executable,str(target/'build_traces.py')],capture_output=True,text=True)
    assert result.returncode!=0 and 'Source changed; review affected diagrams' in result.stderr
    assert (target/'traces.json').read_text()=='preserved'
    assert not (target/'trace-diagrams').exists()
print('Changed source evidence rejects rebuild before overwriting views.')
