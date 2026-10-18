"""Rebuild reviewed views and validate them without refreshing source inventories."""
from pathlib import Path
import subprocess
import sys
from atlas_paths import JAVA, PLANTUML

root=Path(__file__).resolve().parent
def run(*args):
    subprocess.run(args,cwd=root,check=True)

try:
    run(sys.executable,'build_traces.py',*sys.argv[1:])
    for script in ['build_machine.py','build_sequence.py','build_integration.py']:
        run(sys.executable,script)
    for folder in ['diagrams','trace-diagrams','sequence-diagrams']:
        run(JAVA,'-Djava.awt.headless=true','-DPLANTUML_LIMIT_SIZE=12000','-jar',str(PLANTUML),'-tpng',str(root/folder/'*.puml'))
    run(sys.executable,'update_research_state.py')
    run(sys.executable,'check_artifacts.py')
except subprocess.CalledProcessError:
    subprocess.run([sys.executable,'check_artifacts.py'],cwd=root,check=False)
    raise
subprocess.run(['node','scripts/package-architecture.mjs'],cwd=root.parents[1]/'web',check=True)
