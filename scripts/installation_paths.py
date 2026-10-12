"""Shared public host paths for Python installation and operational tools."""
import importlib.util
import os
from pathlib import Path

_spec = importlib.util.spec_from_file_location('laplace_machine', Path(__file__).with_name('machine-env.py'))
_machine = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_machine)


def configuration():
    return {**_machine.paths(_machine.machine_file(os.environ), os.environ, Path(__file__).resolve().parents[1]),
            **{key: value for key, value in os.environ.items() if value}}


def configured(name, suffix=None):
    value = configuration().get(name)
    if not value:
        raise ValueError(f'Declare {name} in {_machine.machine_file(os.environ)} or the process environment')
    path = Path(value)
    if not path.is_absolute():
        raise ValueError(f'{name} must be an absolute path')
    return path / suffix if suffix else path
