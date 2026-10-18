"""Repository-relative sources and machine-configured external build tools."""
from pathlib import Path
import importlib.util
import os
import shutil

ATLAS = Path(__file__).resolve().parent
CHECKOUT = ATLAS.parents[1]
REPOSITORIES = Path(os.environ.get('LAPLACE_ATLAS_REPOSITORIES', str(CHECKOUT.parent)))
spec = importlib.util.spec_from_file_location('laplace_machine_env', CHECKOUT / 'scripts/machine-env.py')
machine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(machine)
settings = machine.paths(machine.machine_file(os.environ), os.environ, CHECKOUT)
work = os.environ.get('LAPLACE_WORK') or settings.get('LAPLACE_WORK')
if not work:
    raise RuntimeError('Configure LAPLACE_WORK through the Laplace machine declaration.')
TOOLS = Path(os.environ.get('LAPLACE_ATLAS_TOOLS', str(Path(work) / 'operation-atlas')))
PLANTUML = Path(os.environ.get('PLANTUML_JAR', str(TOOLS / 'plantuml.jar')))
JAVA = os.environ.get('JAVA') or shutil.which('java') or 'java'
