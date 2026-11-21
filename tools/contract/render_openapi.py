#!/usr/bin/env python3
"""Renders the API reference the admin console serves (src/Nightware.Admin/Api/openapi.json) from the endpoint
contract (docs/contract/endpoints.json): every operation Nightware implements, in OpenAPI 3.1.

    python3 tools/contract/render_openapi.py           # write it
    python3 tools/contract/render_openapi.py --check   # exit 1 when it is out of date

The contract is the whole scope ("implement only what is listed"), so the reference is exactly what a third party
may call. Shapes come from the contract's request and response fields; types are not in the contract and are left
open ({}), as Microsoft's Graph reference (linked from each operation) defines them.
"""
import json
import re
import sys
from collections import OrderedDict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "docs" / "contract" / "endpoints.json"
OUTPUT = ROOT / "src" / "Nightware.Admin" / "Api" / "openapi.json"

AREAS = {
    "mail": "Mail", "calendar": "Calendar", "contacts": "Contacts", "tasks": "To Do", "todo": "To Do",
    "people": "People", "directory": "Directory", "rooms": "Rooms", "settings": "Mailbox settings",
    "mailboxsettings": "Mailbox settings", "mailbox-settings": "Mailbox settings", "files": "Files", "security": "Security", "subscriptions": "Change notifications",
    "batch": "Batching", "schedule": "Calendar",
}

ODATA = OrderedDict([
    ("select", ("$select", "Properties to return, comma-separated.")),
    ("filter", ("$filter", "OData filter (the subset in docs/contract/odata-subset.md).")),
    ("orderby", ("$orderby", "Sort order.")),
    ("top", ("$top", "Page size.")),
    ("search", ("$search", "KQL search, in double quotes.")),
    ("expand", ("$expand", "Related entities to include.")),
])

ERROR_SCHEMA = {
    "type": "object",
    "description": "Graph's error envelope.",
    "properties": {
        "error": {
            "type": "object",
            "properties": {"code": {"type": "string"}, "message": {"type": "string"}, "innerError": {"type": "object"}},
            "required": ["code", "message"],
        }
    },
}


def area(op):
    head = op["id"].split(".")[0].lower()
    return AREAS.get(head, head.capitalize())


def title(op_id):
    """ "mail.messages.update-read-state" -> "Mail messages: update read state"."""
    parts = op_id.replace("-", " ").split(".")
    head = " ".join(parts[:-1]) if len(parts) > 1 else parts[0]
    return (head[:1].upper() + head[1:]) + (": " + parts[-1] if len(parts) > 1 else "")


def nested_schema(fields):
    """Field paths ("value[].id", "body.content", "@odata.nextLink") as one object schema, leaves open."""
    root = {"type": "object", "properties": OrderedDict()}
    for field in fields:
        node = root
        parts = [p for p in re.split(r"\.(?![^\[]*\])", field) if p] if not field.startswith("@") else [field]
        for i, part in enumerate(parts):
            is_array = part.endswith("[]")
            name = part[:-2] if is_array else part
            last = i == len(parts) - 1
            props = node.setdefault("properties", OrderedDict())
            if is_array:
                child = props.setdefault(name, {"type": "array", "items": {"type": "object", "properties": OrderedDict()}})
                node = child["items"]
            elif last:
                props.setdefault(name, {})
            else:
                node = props.setdefault(name, {"type": "object", "properties": OrderedDict()})
    return root


def parameters(op, path):
    params = []
    for name in re.findall(r"\{([^}]+)\}", path):
        params.append({"name": name, "in": "path", "required": True, "schema": {"type": "string"}})
    query = op.get("query") or {}
    for key, (name, description) in ODATA.items():
        values = query.get(key) or []
        if values:
            example = values[0]
            if isinstance(example, list):
                example = ",".join(str(v) for v in example)
            params.append({"name": name, "in": "query", "required": False, "description": description,
                           "schema": {"type": "integer" if key == "top" else "string"}, "example": example})
    for name, values in (query.get("other") or {}).items():
        params.append({"name": name, "in": "query", "required": name in ("startDateTime", "endDateTime"),
                       "schema": {"type": "string"}, "example": values[0] if values else None})
    for name, values in (op.get("headers") or {}).items():
        params.append({"name": name, "in": "header", "required": False, "schema": {"type": "string"},
                       "example": values[0] if isinstance(values, list) and values else values})
    return [{k: v for k, v in p.items() if v is not None} for p in params]


def operation(op, path):
    # NightMail's own operations carry notes about the app's code; only the parity ones describe the server.
    notes = (op.get("notes") or "").strip() if op.get("source") == "graph-parity" else ""
    summary = title(op["id"])
    description = notes or "Behaves as Microsoft Graph's operation on the same route."
    if op.get("docs"):
        description = (description + "\n\n" if description else "") + f"Graph reference: {op['docs']}"
    body = op.get("requestBody")
    result = OrderedDict([
        ("operationId", op["id"] + ("" if path.startswith("/me") or "{userId}" not in path else ".users")),
        ("tags", [area(op)]),
        ("summary", summary),
        ("description", description),
        ("parameters", parameters(op, path)),
    ])
    if body:
        result["requestBody"] = {"required": True, "content": {"application/json": {"schema": nested_schema(body.get("fields") or [])}}}
    status = str(op.get("successStatus") or 200)
    fields = op.get("responseFields") or []
    success = {"description": "Success." + (" Paged by @odata.nextLink." if op.get("paging") == "nextLink" else "")}
    if fields and status != "204":
        success["content"] = {"application/json": {"schema": nested_schema(fields)}}
    result["responses"] = OrderedDict([
        (status, success),
        ("400", {"$ref": "#/components/responses/Error"}),
        ("401", {"$ref": "#/components/responses/Error"}),
        ("403", {"$ref": "#/components/responses/Error"}),
        ("404", {"$ref": "#/components/responses/Error"}),
        ("429", {"$ref": "#/components/responses/Throttled"}),
    ])
    if op.get("version") == "beta":
        result["servers"] = [{"url": "/graph/beta", "description": "Graph beta"}]
    return result


def paths_for(op):
    path = op["path"]
    forms = op.get("forms") or []
    if forms == ["external"]:
        return []  # A pre-signed upload URL the API hands out, not a route to document.
    if path.startswith("{mailbox}"):
        rest = path[len("{mailbox}"):]
        rest = rest if rest.startswith("/") or rest == "" else "/" + rest
        out = []
        if "me" in forms:
            out.append("/me" + rest)
        if "users" in forms:
            out.append("/users/{userId}" + rest)
        return out
    return [path if path.startswith("/") else "/" + path]


def merged(ops):
    """Several contract entries on one route and method (variants of one call) as one operation."""
    if len(ops) == 1:
        return ops[0]
    first = dict(ops[0])
    # The ids' common stem ("subscriptions.create-events", "…-tasks" -> "subscriptions.create").
    stem = ops[0]["id"]
    for other in ops[1:]:
        while not other["id"].startswith(stem):
            stem = stem[:-1]
    first["id"] = stem.rstrip(".-") or ops[0]["id"]
    notes = [f"{title(o['id'])}: {o['notes'].strip()}" for o in ops if o.get("source") == "graph-parity" and (o.get("notes") or "").strip()]
    first["notes"] = "\n\n".join(notes)
    first["source"] = "graph-parity" if notes else ops[0].get("source")
    bodies = [o["requestBody"] for o in ops if o.get("requestBody")]
    if bodies:
        first["requestBody"] = {"fields": list(OrderedDict.fromkeys(f for b in bodies for f in b.get("fields") or []))}
    first["responseFields"] = list(OrderedDict.fromkeys(f for o in ops for f in o.get("responseFields") or []))
    first["docs"] = next((o["docs"] for o in ops if o.get("docs")), None)
    return first


def render():
    contract = json.loads(CONTRACT.read_text())
    grouped = OrderedDict()
    for op in sorted(contract, key=lambda o: (area(o), o["id"])):
        for path in paths_for(op):
            grouped.setdefault((path, op["method"].lower()), []).append(op)
    paths = OrderedDict()
    used = set()
    for (path, method), ops in grouped.items():
        built = operation(merged(ops), path)
        # Operation ids are unique in OpenAPI: a stem shared by several routes takes its method, then a number.
        base = built["operationId"]
        candidate = base if base not in used else f"{base}.{method}"
        n = 2
        while candidate in used:
            candidate, n = f"{base}.{method}{n}", n + 1
        used.add(candidate)
        built["operationId"] = candidate
        paths.setdefault(path, OrderedDict())[method] = built
    document = OrderedDict([
        ("openapi", "3.1.0"),
        ("info", OrderedDict([
            ("title", "Nightware Graph API"),
            ("version", "v1.0"),
            ("description", (
                "Nightware answers the Microsoft Graph API for mail, calendars, contacts, To Do, people and change "
                "notifications, so a Graph client can use it by changing its base URL and sign-in authority. This "
                "reference lists every operation Nightware implements; anything else answers 501. Shapes follow "
                "Microsoft's Graph reference, linked from each operation where it differs from NightMail's own use.\n\n"
                "Authenticate with a bearer token from the Nightware Keycloak realm (OAuth 2.0 authorization code with "
                "PKCE; discovery at /.well-known/nightware). Paste one into Authorize to try a call."
            )),
        ])),
        ("servers", [{"url": "/graph/v1.0", "description": "This server"}]),
        ("security", [{"bearer": []}]),
        ("tags", [{"name": t} for t in sorted({area(o) for o in contract})]),
        ("paths", paths),
        ("components", OrderedDict([
            ("securitySchemes", {"bearer": {"type": "http", "scheme": "bearer", "bearerFormat": "JWT",
                                            "description": "An access token for audience nightware-api from the Keycloak realm."}}),
            ("schemas", {"Error": ERROR_SCHEMA}),
            ("responses", OrderedDict([
                ("Error", {"description": "A Graph error.", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Error"}}}}),
                ("Throttled", {"description": "Too many requests; wait Retry-After seconds.",
                               "headers": {"Retry-After": {"schema": {"type": "integer"}}},
                               "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Error"}}}}),
            ])),
        ])),
    ])
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def main():
    rendered = render()
    if "--check" in sys.argv:
        current = OUTPUT.read_text() if OUTPUT.exists() else ""
        if current != rendered:
            print(f"{OUTPUT.relative_to(ROOT)} is out of date: run python3 tools/contract/render_openapi.py", file=sys.stderr)
            return 1
        return 0
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(rendered)
    print(f"wrote {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
