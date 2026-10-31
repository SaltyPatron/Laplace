"""Locate the separate Wiki checkout used by document consumers."""

import os
from pathlib import Path


def documentation_root(source: Path) -> Path:
    configured = os.environ.get("LAPLACE_DOCUMENTATION_ROOT")
    if configured:
        return Path(configured).resolve()
    dependency = source / ".ci-dependencies/Laplace-Wiki/Repositories/Laplace"
    if dependency.is_dir():
        return dependency.resolve()
    return (source.parent / "Laplace-Wiki/Repositories/Laplace").resolve()
