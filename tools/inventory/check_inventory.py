#!/usr/bin/env python3
"""Check docs/contract/endpoints.json against the NightMail client.

Re-greps the client for every Microsoft Graph request path and fails (exit 1)
when:

  * a path the client requests is not covered by an operation in
    endpoints.json (taking `forms` into account: a `{mailbox}` path must be
    declared for both `me` and `users`, because the client's `_base` can be
    either);
  * an operation has no fixture in tests/Nightware.Conformance.Tests/Fixtures,
    or a fixture names an operation id that does not exist;
  * endpoints.json or a fixture is not valid JSON, or an operation is missing a
    required key.

Standard library only. Usage:

    python3 tools/inventory/check_inventory.py [--client PATH] [--list]

--client defaults to $NIGHTMAIL_SRC, then ~/Source/nightmail.
--list prints every extracted call path with its file:line and the operation(s)
it matched, then runs the checks as usual.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ENDPOINTS = REPO / "docs" / "contract" / "endpoints.json"
FIXTURES = REPO / "tests" / "Nightware.Conformance.Tests" / "Fixtures"

REQUIRED_KEYS = {
    "id", "phase", "method", "version", "path", "forms", "query", "headers",
    "requestBody", "responseFields", "paging", "clientCallSites", "notes",
}
VALID_PHASES = {2, 3, 4, 5, "5A", "5B", 11, 12, 13, 14, 15, 16, 18}
# ADR 0006 D1: operations NightMail does not call, added for Graph parity.
GRAPH_PARITY = "graph-parity"
VALID_FORMS = {"me", "users", "root", "external"}

# Files scanned for *relative* Graph paths: the datasources that issue requests
# through GraphHttpClient (base URL https://graph.microsoft.com/v1.0).
GRAPH_DATASOURCE_GLOB = "lib/data/datasources/remote/graph_*.dart"
GRAPH_CLIENT_CTOR = re.compile(r"required\s+GraphHttpClient\s+client")

# Absolute Graph URLs are collected from every file under lib/.
ABSOLUTE_GRAPH = re.compile(r"^https://graph\.microsoft\.com/(v1\.0|beta)(/.*)?$")
# OAuth scope strings (https://graph.microsoft.com/Mail.Read) are not calls.
SCOPE_STRING = re.compile(r"^https://graph\.microsoft\.com/[A-Z]")

# A literal that starts with an interpolated Graph root, e.g.
# '${_endpoints.graphBetaBaseUrl}/me/findRooms' or '$graphBaseUrl/me'.
CONFIGURED_ROOT = re.compile(r"^\$\{?([A-Za-z_][\w.]*)\}?")
# Names of a configurable *Graph* root. Deliberately not a bare `baseUrl`:
# MicrosoftAuthService's `_baseUrl` is the login authority, not Graph.
ROOT_NAME = re.compile(r"(?i)graph\w*baseurl|betabase|graphbase|v1base|graphroot")

# Lines that construct the mailbox base rather than request a path:
# `_base = mailboxAddress == null ? '/me' : '/users/$x'`,
# `[String mailboxBase = '/me']`.
NOT_A_REQUEST_LINE = re.compile(r"[Bb]ase\s*=\s*")
# A literal that is the argument of a string test, not a request target:
# `_base.startsWith('/users/')`, `_base.substring('/users/'.length)`.
STRING_TEST_ARG = re.compile(r"\.(?:startsWith|substring|endsWith|contains)\(\s*$")

# Dart string literals (single- or double-quoted), optionally raw.
DART_LITERAL = re.compile(r"(r?)(['\"])((?:(?!\2)[^\\\n]|\\.)*)\2")
# `_dio.get(_base, ...)`: the mailbox root itself, passed as a bare identifier.
BARE_BASE_CALL = re.compile(r"_dio\.\w+(?:<[^>]*>)?\(\s*$|^\s*_base\s*,\s*$")

# Literals that look like paths but are not request targets. Keyed by
# (file suffix, normalised path); each needs a reason.
NOT_A_CALL = {
    # Body value of the threat submission (`messageUrl`), not a request URL.
    # Recorded on security.threat-submission.email-threats.create instead.
    ("graph_api_datasource_impl.dart", "beta:/{}/messages/{}"),
}


def normalise(literal: str, raw: bool) -> tuple[str, str] | None:
    """Return (version, path) for a literal, or None if it is not a path."""
    s = literal
    if not raw:
        s = s.replace("\\$", "$")
    version = "v1.0"
    m = ABSOLUTE_GRAPH.match(s)
    root = CONFIGURED_ROOT.match(s)
    if root and not ROOT_NAME.search(root.group(1)):
        root = None
    if m:
        version = m.group(1)
        s = m.group(2) or "/"
    elif root:
        # A configurable Graph root (NightMail's GraphEndpoints): beta when the
        # interpolated name says so, v1.0 otherwise.
        version = "beta" if re.search(r"beta", root.group(1), re.I) else "v1.0"
        s = s[root.end():] or "/"
    elif s.startswith("https://"):
        return None
    for base in ("${_base}", "$_base", "${mailboxBase}", "$mailboxBase"):
        if s.startswith(base):
            s = "{mailbox}" + s[len(base):]
            break
    if not (s.startswith("/") or s.startswith("{mailbox}")):
        return None
    s = s.split("?", 1)[0]
    # Interpolations become a wildcard segment marker.
    s = re.sub(r"\$\{[^}]*\}", "{}", s)
    s = re.sub(r"\$[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*", "{}", s)
    s = s.replace("{}{}", "{}")
    if len(s) > 1:
        s = s.rstrip("/")
    return version, s


class Source:
    """The client's lib/ tree: the working tree, or a git revision of it."""

    def __init__(self, client: Path, rev: str | None):
        self.client, self.rev = client, rev

    def dart_files(self) -> list[str]:
        if self.rev:
            out = subprocess.run(
                ["git", "-C", str(self.client), "ls-tree", "-r", "--name-only",
                 self.rev, "lib"], check=True, capture_output=True, text=True)
            return sorted(p for p in out.stdout.splitlines()
                          if p.endswith(".dart"))
        lib = self.client / "lib"
        return sorted(p.relative_to(self.client).as_posix()
                      for p in lib.rglob("*.dart"))

    def read(self, rel: str) -> str:
        if self.rev:
            return subprocess.run(
                ["git", "-C", str(self.client), "show", f"{self.rev}:{rel}"],
                check=True, capture_output=True, text=True).stdout
        return (self.client / rel).read_text(encoding="utf-8", errors="replace")


HTTP_CALL = re.compile(
    r"\b(?:_dio|_http|http|_preAuth|uploadDio|dio)\.(get|post|patch|put|delete)"
    r"\s*(?:<[^(]*?>)?\s*\(")


# Client helpers that issue a GET for the path they are handed.
GET_HELPERS = re.compile(
    r"\b(?:_fetchAllGraphPages|collect|_eventIdByNavigation)\s*\(")


CONST_PATH = re.compile(r"\b(?:const|final)\s+(?:String\s+)?(\w+)\s*=\s*r?'")


def collapse_interpolations(line: str) -> str:
    """Replace each `${...}` (balanced braces, nested quotes allowed) with
    `${_}`, so a literal such as '$_base/m/${id.replaceAll('x', 'y')}/e' is
    read as one string rather than split at its inner quotes. Root-like
    names are kept so a configurable Graph root is still recognised."""
    out, i = [], 0
    while i < len(line):
        if line.startswith("${", i):
            depth, j = 1, i + 2
            while j < len(line) and depth:
                depth += {"{": 1, "}": -1}.get(line[j], 0)
                j += 1
            inner = line[i + 2:j - 1]
            keep = inner if re.fullmatch(r"[A-Za-z_][\w.]*", inner) else "_"
            out.append("${" + keep + "}")
            i = j
        else:
            out.append(line[i])
            i += 1
    return "".join(out)


def const_version(text: str, line: str) -> str | None:
    """Version of a path held in a named constant (`_fooPath = '/x'`), read
    from where it is joined onto a root: `'$_betaBase$_fooPath'` → beta."""
    m = CONST_PATH.search(line)
    if not m:
        return None
    name = re.escape(m.group(1))
    joined = re.findall(r"\$\{?([A-Za-z_][\w.]*)\}?\$\{?" + name + r"\b", text)
    for root in joined:
        if ROOT_NAME.search(root):
            return "beta" if re.search(r"(?i)beta", root) else "v1.0"
    return None


def method_near(lines: list[str], idx: int) -> str | None:
    """HTTP method of the request a path literal on lines[idx] belongs to.

    Looks at the line itself, then outward (backwards first) up to 6 lines:
    a path is either the first argument of `_dio.get(`/`post(`/... or held in
    a local (`final path = ...`, a `for (final path in [...])` list) that the
    next call uses.
    """
    for dist in range(0, 7):
        for j in (idx - dist, idx + dist):
            if 0 <= j < len(lines):
                m = HTTP_CALL.search(lines[j])
                if m:
                    return m.group(1).upper()
                if GET_HELPERS.search(lines[j]):
                    return "GET"
    return None


def extract_calls(src: Source) -> list[tuple[str | None, str, str, str]]:
    """(method, version, path, 'file:line') for every Graph request path."""
    out: list[tuple[str | None, str, str, str]] = []
    files = src.dart_files()
    texts = {rel: src.read(rel) for rel in files}
    graph_files = {rel for rel in files
                   if fnmatch.fnmatch(rel, GRAPH_DATASOURCE_GLOB)
                   or GRAPH_CLIENT_CTOR.search(texts[rel])}
    for rel in files:
        full_scan = rel in graph_files
        lines = texts[rel].splitlines()
        name = rel.rsplit("/", 1)[-1]
        for n, line in enumerate(lines, 1):
            stripped = line.lstrip()
            if stripped.startswith("//") or NOT_A_REQUEST_LINE.search(line):
                continue
            line = collapse_interpolations(line)
            for lit in DART_LITERAL.finditer(line):
                raw_flag, body = lit.group(1), lit.group(3)
                if STRING_TEST_ARG.search(line[:lit.start()]):
                    continue
                raw = raw_flag == "r"
                candidate = body if raw else body.replace("\\$", "$")
                if SCOPE_STRING.match(candidate):
                    continue
                root = CONFIGURED_ROOT.match(candidate)
                is_abs = bool(ABSOLUTE_GRAPH.match(candidate)) or bool(
                    root and ROOT_NAME.search(root.group(1)))
                if not is_abs and not full_scan:
                    continue
                norm = normalise(body, raw)
                if norm is None:
                    continue
                version, path = norm
                version = const_version(texts[rel], line) or version
                if path == "/":
                    continue  # a bare root (base URL constant), not a call
                if (name, f"{version}:{path}") in NOT_A_CALL:
                    continue
                out.append((method_near(lines, n - 1), version, path,
                            f"{rel}:{n}"))
            if full_scan and re.match(r"^\s*_base\s*,\s*$", line):
                out.append((method_near(lines, n - 1), "v1.0", "{mailbox}",
                            f"{rel}:{n}"))
    return out


def op_variants(op: dict) -> list[tuple[str, list[str]]]:
    """Concrete (version, segments) for each declared form of an operation."""
    path: str = op["path"]
    forms = op["forms"]
    variants = []
    if path.startswith("{mailbox}"):
        rest = path[len("{mailbox}"):]
        if "me" in forms:
            variants.append("/me" + rest)
        if "users" in forms:
            variants.append("/users/{mailbox}" + rest)
    else:
        variants.append(path)
    return [(op["version"], v.strip("/").split("/") if v != "/" else [])
            for v in variants]


def seg_match(client_seg: str, op_seg: str) -> bool:
    if client_seg == "{}":
        return True
    if op_seg.startswith("{") and op_seg.endswith("}"):
        return True
    return client_seg == op_seg


def path_matches(version: str, client_segs: list[str], op: dict) -> bool:
    for v, segs in op_variants(op):
        if v != version or len(segs) != len(client_segs):
            continue
        if all(seg_match(c, o) for c, o in zip(client_segs, segs)):
            return True
    return False


def covered(method: str | None, version: str, path: str,
            ops: list[dict]) -> tuple[bool, list[str]]:
    """A `{mailbox}` client path must be covered in *both* forms.

    When the method could not be read off the source (a helper such as
    `linkedEventPath`), any method matches."""
    if path.startswith("{mailbox}"):
        rest = path[len("{mailbox}"):]
        forms = ["/me" + rest, "/users/{}" + rest]
    else:
        forms = [path]
    matched: list[str] = []
    for form in forms:
        segs = form.strip("/").split("/") if form != "/" else []
        hits = [op["id"] for op in ops
                if (method is None or op["method"] == method)
                and path_matches(version, segs, op)]
        if not hits:
            return False, []
        matched.extend(h for h in hits if h not in matched)
    return True, matched


def load_json(path: Path, errors: list[str]):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        errors.append(f"{path.relative_to(REPO)}: invalid JSON ({e})")
        return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--client", default=os.environ.get(
        "NIGHTMAIL_SRC", str(Path.home() / "Source" / "nightmail")))
    ap.add_argument("--rev", help="scan this git revision of the client "
                    "(e.g. HEAD) instead of its working tree")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()
    client = Path(args.client).expanduser()
    if not (client / "lib").is_dir():
        print(f"error: {client} is not a NightMail checkout (no lib/)",
              file=sys.stderr)
        return 2

    errors: list[str] = []
    warnings: list[str] = []
    ops = load_json(ENDPOINTS, errors)
    if ops is None:
        print("\n".join(errors), file=sys.stderr)
        return 1

    ids: set[str] = set()
    for i, op in enumerate(ops):
        missing = REQUIRED_KEYS - op.keys()
        if missing:
            errors.append(f"operation #{i} ({op.get('id')}): missing keys "
                          f"{sorted(missing)}")
            continue
        if op["id"] in ids:
            errors.append(f"duplicate operation id {op['id']}")
        ids.add(op["id"])
        if op["phase"] not in VALID_PHASES:
            errors.append(f"{op['id']}: invalid phase {op['phase']!r}")
        if not set(op["forms"]) <= VALID_FORMS or not op["forms"]:
            errors.append(f"{op['id']}: invalid forms {op['forms']}")
        if op["path"].startswith("{mailbox}") and not (
                {"me", "users"} & set(op["forms"])):
            errors.append(f"{op['id']}: {{mailbox}} path needs me/users form")
        if op["paging"] not in ("nextLink", "deltaLink", "none"):
            errors.append(f"{op['id']}: invalid paging {op['paging']!r}")
        parity = op.get("source") == GRAPH_PARITY
        if parity and (op["clientCallSites"] or not op.get("docs")):
            errors.append(f"{op['id']}: a graph-parity operation has no clientCallSites and a 'docs' link")
        if not parity and not op["clientCallSites"]:
            errors.append(f"{op['id']}: no clientCallSites")

    # Fixtures: one per operation, and none orphaned.
    fixture_ids: set[str] = set()
    for fx in sorted(FIXTURES.glob("*.json")):
        data = load_json(fx, errors)
        if data is None:
            continue
        oid = data.get("operationId")
        if oid != fx.stem:
            errors.append(f"fixture {fx.name}: operationId {oid!r} does not "
                          f"match file name")
        for key in ("provenance", "request", "response"):
            if key not in data:
                errors.append(f"fixture {fx.name}: missing {key!r}")
        resp = data.get("response", {})
        if "status" not in resp:
            errors.append(f"fixture {fx.name}: response.status missing")
        fixture_ids.add(fx.stem)
    for oid in sorted(ids - fixture_ids):
        errors.append(f"operation {oid} has no fixture ({oid}.json)")
    for oid in sorted(fixture_ids - ids):
        errors.append(f"fixture {oid}.json names no operation in endpoints.json")

    # Client call paths must all be covered.
    calls = extract_calls(Source(client, args.rev))
    used_ops: set[str] = set()
    # `external` operations (an absolute URL handed out by a response, such as
    # an upload session) are not routes the client builds, so never match.
    valid_ops = [o for o in ops if REQUIRED_KEYS <= o.keys()
                 and "external" not in o["forms"]]
    for method, version, path, site in calls:
        ok, hits = covered(method, version, path, valid_ops)
        used_ops.update(hits)
        if args.list:
            print(f"{site:62} {method or '*':6} {version:4} {path:62} -> "
                  f"{', '.join(hits) if ok else 'UNCOVERED'}")
        if not ok:
            errors.append(f"uncovered client call {method or '*'} {version} "
                          f"{path} at {site}")

    for op in ops:
        if REQUIRED_KEYS <= op.keys() and op["id"] not in used_ops and \
                "external" not in op["forms"] and op.get("source") != GRAPH_PARITY:
            warnings.append(f"operation {op['id']} matched no extracted "
                            "call path (absolute nextLink/uploadUrl targets "
                            "are expected here)")

    for w in warnings:
        print(f"warning: {w}")
    if errors:
        for e in errors:
            print(f"error: {e}", file=sys.stderr)
        print(f"FAILED: {len(errors)} error(s)", file=sys.stderr)
        return 1
    print(f"OK: {len(calls)} client call paths covered by {len(ids)} "
          f"operations; every operation has a fixture.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
