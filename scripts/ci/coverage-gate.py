#!/usr/bin/env python3
"""Fail the build when merged line or branch coverage drops below the gate.

usage: coverage-gate.py <merged.cobertura.xml> <min-line-%> <min-branch-%>

Prints a per-assembly table (also appended to $GITHUB_STEP_SUMMARY when set).
The thresholds and why they were chosen live in docs/operations/ci.md.
"""
import os
import sys
import xml.etree.ElementTree as ET


def pct(value: str | None) -> float:
    return 100.0 * float(value or 0)


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    path, min_line, min_branch = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
    root = ET.parse(path).getroot()
    if root.get("lines-valid") in (None, "0"):
        print(f"coverage-gate: {path} has no measured lines; was coverage collected?", file=sys.stderr)
        return 1

    rows = ["| Assembly | Line | Branch |", "|---|---:|---:|"]
    for package in sorted(root.iter("package"), key=lambda p: p.get("name", "")):
        rows.append(f"| {package.get('name')} | {pct(package.get('line-rate')):.1f}% | {pct(package.get('branch-rate')):.1f}% |")
    line, branch = pct(root.get("line-rate")), pct(root.get("branch-rate"))
    rows.append(f"| **Total** | **{line:.1f}%** (gate {min_line:.0f}%) | **{branch:.1f}%** (gate {min_branch:.0f}%) |")
    table = "\n".join(rows)
    print(table)

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as out:
            out.write("### Coverage\n\n" + table + "\n")

    failures = []
    if line < min_line:
        failures.append(f"line coverage {line:.2f}% < {min_line}%")
    if branch < min_branch:
        failures.append(f"branch coverage {branch:.2f}% < {min_branch}%")
    for failure in failures:
        print(f"coverage-gate: {failure}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
