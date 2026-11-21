"""Appends graph-parity operations to docs/contract/endpoints.json (ADR 0006 D1).

Usage from a phase script:  from parity_ops import add; add(phase, [(id, method, path, docs, body, fields, status, notes, paging?), ...])
"""
import json
from pathlib import Path

ENDPOINTS = Path(__file__).resolve().parents[2] / "docs" / "contract" / "endpoints.json"


def add(phase, operations, forms=("me", "users")):
    ops = json.loads(ENDPOINTS.read_text())
    known = {o["id"] for o in ops}
    for entry in operations:
        op_id, method, path, docs, body, fields, status, notes = entry[:8]
        paging = entry[8] if len(entry) > 8 else "none"
        if op_id in known:
            continue
        ops.append(dict(
            id=op_id, phase=phase, method=method, version="v1.0", path=path,
            forms=list(forms) if path.startswith("{mailbox}") else ["root"],
            query={"select": [], "filter": [], "orderby": [], "top": [], "search": [], "expand": [], "other": {}},
            headers={}, requestBody={"fields": body} if body else None, responseFields=fields, successStatus=status,
            paging=paging, clientErrorHandling="", clientCallSites=[], source="graph-parity", docs=docs, notes=notes))
    ENDPOINTS.write_text(json.dumps(ops, indent=2, ensure_ascii=False) + "\n")
