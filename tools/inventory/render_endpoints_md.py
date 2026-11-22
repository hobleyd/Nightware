#!/usr/bin/env python3
"""Regenerate the per-phase operation tables in docs/contract/endpoints.md.

endpoints.json is the source of truth; this rewrites only the text between the
`<!-- BEGIN GENERATED -->` / `<!-- END GENERATED -->` markers, leaving the
hand-written sections (conventions, client error expectations, …) alone.
Standard library only.  Usage: python3 tools/inventory/render_endpoints_md.py
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
JSON_PATH = REPO / "docs" / "contract" / "endpoints.json"
MD_PATH = REPO / "docs" / "contract" / "endpoints.md"
BEGIN, END = "<!-- BEGIN GENERATED -->", "<!-- END GENERATED -->"

PHASES = [
    ("2", "Phase 2 — Mail, read side"),
    ("3", "Phase 3 — Mail, write side"),
    ("4", "Phase 4 — Calendar, scheduling and rooms"),
    ("5A", "Phase 5A — Directory, contacts, people"),
    ("5B", "Phase 5B — Tasks, mailbox settings, files, threat submission"),
    ("11", "Phase 11 — JSON batching (Graph parity)"),
    ("12", "Phase 12 — Mail (Graph parity)"),
    ("13", "Phase 13 — Calendar (Graph parity)"),
    ("14", "Phase 14 — Contacts (Graph parity)"),
    ("15", "Phase 15 — To Do (Graph parity)"),
    ("16", "Phase 16 — Change notifications (Graph parity)"),
    ("18", "Phase 18 — Settings, aliases, single items and $count (Graph parity)"),
    ("19", "Phase 19 — Photos, MIME, copies, $count segments and more aliases (Graph parity)"),
    ("21", "Phase 21 — The directory: groups, manager, organization (Graph parity)"),
]


def code(s: str) -> str:
    return f"`{s}`"


def render_query(q: dict) -> list[str]:
    out = []
    for i, sel in enumerate(q.get("select") or []):
        out.append(f"`$select` ({'variant ' + str(i + 1) if len(q['select']) > 1 else 'always'}): "
                   + code(",".join(sel)))
    for key, label in (("filter", "$filter"), ("orderby", "$orderby"),
                       ("search", "$search"), ("expand", "$expand")):
        for v in q.get(key) or []:
            out.append(f"`{label}`: {code(v)}")
    if q.get("top"):
        out.append("`$top`: " + ", ".join(code(str(t)) for t in q["top"]))
    for k, vals in (q.get("other") or {}).items():
        out.append(f"`{k}`: " + ", ".join(code(str(v)) for v in vals))
    return out


def render_op(op: dict) -> str:
    lines = [f"#### `{op['id']}`", ""]
    forms = ", ".join(op["forms"])
    lines.append(f"**{op['method']} {op['version']} `{op['path']}`** — forms: {forms}; "
                 f"success: {op.get('successStatus', '')}; paging: {op['paging']}")
    lines.append("")
    q = render_query(op["query"])
    if q:
        lines.append("- Query: " + "; ".join(q))
    if op["headers"]:
        lines.append("- Headers: " + "; ".join(
            f"`{k}: {v}`" for k, vals in op["headers"].items() for v in vals))
    rb = op.get("requestBody")
    if rb:
        lines.append("- Request body: " + "; ".join(code(f) for f in rb.get("fields", [])))
        if rb.get("headers"):
            lines.append("- Request headers: " + "; ".join(code(h) for h in rb["headers"]))
    if op["responseFields"]:
        lines.append("- Client reads: " + ", ".join(code(f) for f in op["responseFields"]))
    if op.get("clientErrorHandling"):
        lines.append("- On error: " + op["clientErrorHandling"])
    if op.get("source") == "graph-parity":
        lines.append(f"- Graph parity (ADR 0006); reference: {op['docs']}")
    else:
        lines.append("- Call sites: " + ", ".join(
            code(s.replace("lib/data/datasources/remote/", "…/remote/")) for s in op["clientCallSites"]))
    if op["notes"]:
        lines.append("- Notes: " + op["notes"])
    lines.append(f"- Fixture: `tests/Nightware.Conformance.Tests/Fixtures/{op['id']}.json`")
    lines.append("")
    return "\n".join(lines)


def render(ops: list[dict]) -> str:
    parts = [BEGIN, ""]
    parts.append("| Phase | Operations |")
    parts.append("|---|---|")
    for key, title in PHASES:
        n = sum(1 for o in ops if str(o["phase"]) == key)
        if n:
            parts.append(f"| {title} | {n} |")
    parts.append(f"| **Total** | **{len(ops)}** |")
    parts.append("")
    for key, title in PHASES:
        group = [o for o in ops if str(o["phase"]) == key]
        if not group:
            continue
        parts.append(f"### {title}")
        parts.append("")
        parts.append("| Id | Method | Ver | Path | Paging |")
        parts.append("|---|---|---|---|---|")
        for o in group:
            parts.append(f"| [`{o['id']}`](#{o['id'].replace('.', '')}) | {o['method']} | "
                         f"{o['version']} | `{o['path']}` | {o['paging']} |")
        parts.append("")
        for o in group:
            parts.append(render_op(o))
    parts.append(END)
    return "\n".join(parts)


def main() -> int:
    ops = json.loads(JSON_PATH.read_text(encoding="utf-8"))
    md = MD_PATH.read_text(encoding="utf-8")
    if BEGIN not in md or END not in md:
        print(f"error: markers missing from {MD_PATH}", file=sys.stderr)
        return 1
    head, rest = md.split(BEGIN, 1)
    _, tail = rest.split(END, 1)
    MD_PATH.write_text(head + render(ops) + tail, encoding="utf-8")
    print(f"rendered {len(ops)} operations into {MD_PATH.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
