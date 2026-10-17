#!/usr/bin/env python3
"""Summarize the .trx results under a directory: totals per assembly, then each failed test with the first line of its
message, grouped by that line. Exit 1 if any test failed or no test ran."""
import collections
import pathlib
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def main(root: str) -> int:
    files = sorted(pathlib.Path(root).rglob("*.trx"))
    total = failed = 0
    groups: dict[str, list[str]] = collections.defaultdict(list)
    for f in files:
        tree = ET.parse(f).getroot()
        counters = tree.find(f".//{NS}Counters")
        storage = next((t.get("storage", "") for t in tree.iter(f"{NS}UnitTest")), "")
        n, bad = int(counters.get("total", 0)), int(counters.get("failed", 0))
        total += n; failed += bad
        print(f"{pathlib.Path(storage).name or f.name}: total {n}, passed {counters.get('passed')}, failed {bad}, "
              f"not executed {counters.get('notExecuted')}")
        for r in tree.iter(f"{NS}UnitTestResult"):
            if r.get("outcome") != "Failed":
                continue
            msg = r.find(f"{NS}Output/{NS}ErrorInfo/{NS}Message")
            line = (msg.text or "").strip().splitlines()[0][:200] if msg is not None and msg.text else "(no message)"
            groups[line].append(r.get("testName", "?"))
    for line, tests in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        print(f"\n[{len(tests)}] {line}")
        for t in tests[:8]:
            print(f"    {t}")
        if len(tests) > 8:
            print(f"    ... {len(tests) - 8} more")
    print(f"\n{len(files)} result files, {total} tests, {failed} failed")
    return 1 if failed or total == 0 else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
