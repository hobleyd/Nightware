#!/usr/bin/env python3
"""Record Microsoft Graph request/response pairs as conformance fixtures.

Runs the scenarios in scenarios.py against a real Microsoft 365 mailbox (ADR
0006 D2), scrubs everything that identifies the tenant, and writes one fixture
per operation id in the format of tests/Nightware.Conformance.Tests/Fixtures.
Fixtures go to tools/capture/out/ (not the conformance folder): a phase moves
them across together with their endpoints.json entries.

Standard library only. Usage:

    GRAPH_TOKEN=eyJ... python3 tools/capture/capture_graph.py --phase 12
    python3 tools/capture/capture_graph.py --phase all --dry-run

See tools/capture/README.md for getting a token and capturing notifications.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import json
import os
import queue
import re
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import scenarios  # noqa: E402

GRAPH = "https://graph.microsoft.com"
OUT = Path(__file__).resolve().parent / "out"
CLIENT_STATE = "nightware-capture-state"
FAKE_NOTIFY_URL = "https://webhook.example.com/notify"
# Keys whose string values are Graph ids: mapped to stable placeholders everywhere.
ID_KEYS = re.compile(r"^(id|.*Id|changeKey|iCalUId)$")
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
TOKEN_PARAMS = ("$skiptoken", "$deltatoken", "skiptoken", "deltatoken")
SAFE_PATH = "/()=',:$@!*+;-._~"


class StepFailed(Exception):
    pass


@dataclass
class Exchange:
    """One recorded request/response, kept raw until the whole run is scrubbed."""

    op: str
    request: dict
    response: dict
    direction: str = "inbound"


@dataclass
class Run:
    token: str
    dry_run: bool
    notification_url: str | None
    colleague: str | None
    photo: bytes
    vars: dict = field(default_factory=dict)
    exchanges: list[Exchange] = field(default_factory=list)
    notifications: "queue.Queue[dict]" = field(default_factory=queue.Queue)


# ---------------------------------------------------------------- substitution

VAR = re.compile(r"\$\{([A-Za-z0-9_.]+)\}")


def subst(value, run: Run):
    if isinstance(value, str):
        def repl(m: re.Match) -> str:
            name = m.group(1)
            if name not in run.vars:
                raise StepFailed(f"unset variable ${{{name}}}")
            return str(run.vars[name])
        return VAR.sub(repl, value)
    if isinstance(value, list):
        return [subst(v, run) for v in value]
    if isinstance(value, dict):
        return {subst(k, run): subst(v, run) for k, v in value.items()}
    return value


def pick(body, path: str):
    """Reads 'value[0].id' / '@odata.deltaLink' style paths; None when absent."""
    if isinstance(body, dict) and path in body:
        return body[path]
    node = body
    for part in re.findall(r"[^.\[\]]+|\[\d+\]", path):
        if part.startswith("["):
            idx = int(part[1:-1])
            if not isinstance(node, list) or idx >= len(node):
                return None
            node = node[idx]
        else:
            if not isinstance(node, dict) or part not in node:
                return None
            node = node[part]
    return node


# ---------------------------------------------------------------------- HTTP

def send(method: str, url: str, headers: dict, data: bytes | None) -> tuple[int, dict, bytes]:
    for attempt in range(5):
        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                return resp.status, dict(resp.headers), resp.read()
        except urllib.error.HTTPError as err:
            if err.code in (429, 503, 504) and attempt < 4:
                time.sleep(int(err.headers.get("Retry-After", "5")))
                continue
            return err.code, dict(err.headers), err.read()
    raise StepFailed(f"{method} {url}: gave up after retries")


def header(headers: dict, name: str) -> str | None:
    for k, v in headers.items():
        if k.lower() == name.lower():
            return v
    return None


def decode_body(headers: dict, raw: bytes):
    ctype = header(headers, "Content-Type") or ""
    if not raw:
        return None
    if "json" in ctype:
        return json.loads(raw)
    if ctype.startswith("text/"):
        return raw.decode("utf-8", "replace")
    return {"$binary": {"length": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}}


def binary_payload(spec: dict, run: Run) -> bytes:
    if spec.get("photo"):
        return run.photo
    start, end = spec["range"]
    return bytes(end - start + 1)


# --------------------------------------------------------------------- steps

def run_step(step: dict, run: Run) -> None:
    if step.get("requires") and not getattr(run, step["requires"]):
        print(f"    skip {step.get('op') or step['method']}: needs --{step['requires'].replace('_', '-')}")
        return
    if "wait_notification" in step:
        wait_notification(step, run)
        return

    method = step["method"]
    auth = step.get("auth", True)
    if "url" in step:
        url = subst(step["url"], run)
    else:
        path = subst(step["path"], run)
        url = GRAPH + urllib.parse.quote(path, safe=SAFE_PATH)
        query = subst(step.get("query", {}), run)
        if query:
            url += "?" + urllib.parse.urlencode(query, quote_via=urllib.parse.quote, safe="$,'()=/:")

    headers = {"Accept": "application/json"}
    if auth:
        headers["Authorization"] = f"Bearer {run.token}"
    headers.update(subst(step.get("headers", {}), run))
    data = None
    sent_body = None
    if "body" in step:
        sent_body = subst(step["body"], run)
        data = json.dumps(sent_body).encode()
        headers.setdefault("Content-Type", "application/json")
    elif "mime" in step:
        # Graph's MIME form: the message base64-encoded, sent as text/plain; built after substitution.
        spec = subst(step["mime"], run)
        message = (f"To: {spec['to']}\r\nSubject: {spec['subject']}\r\nMIME-Version: 1.0\r\n"
                   f"Content-Type: text/plain; charset=utf-8\r\n\r\n{spec.get('body', 'Sent as MIME.')}\r\n")
        sent_body = base64.b64encode(message.encode()).decode()
        data = sent_body.encode()
        headers.setdefault("Content-Type", "text/plain")
    elif "binary" in step:
        data = binary_payload(step["binary"], run)
        sent_body = {"$binary": {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}}
        headers.setdefault("Content-Type", step["binary"].get("contentType", "application/octet-stream"))

    label = step.get("op") or f"({method} {step.get('path') or step.get('url')})"
    if run.dry_run:
        print(f"    {label}: {method} {url}")
        for var in [*step.get("save", {}), *([step["follow_to"]] if step.get("follow_to") else [])]:
            run.vars[var] = f"<{var}>"
        return

    tries = step.get("poll", {}).get("tries", 1)
    for attempt in range(tries):
        status, resp_headers, raw = send(method, url, headers, data)
        body = decode_body(resp_headers, raw)
        if "poll" not in step or pick(body, step["poll"]["until"]) is not None:
            break
        time.sleep(step["poll"].get("interval", 3))
    else:
        raise StepFailed(f"{label}: '{step['poll']['until']}' never appeared")

    expect = step.get("expect", [200, 201, 202, 204])
    print(f"    {label}: {status}")
    if status not in expect:
        raise StepFailed(f"{label}: HTTP {status} {raw[:500]!r}")

    for var, path in step.get("save", {}).items():
        value = pick(body, path)
        if value is None:
            raise StepFailed(f"{label}: no '{path}' in response to save as {var}")
        run.vars[var] = value

    if step.get("follow_to"):
        link = follow(body, run, headers)
        run.vars[step["follow_to"]] = link

    if step.get("op"):
        parsed = urllib.parse.urlsplit(url)
        record_path = step.get("record_path") or parsed.path
        recorded_headers = {k: v for k, v in headers.items() if k != "Authorization"}
        if auth:
            recorded_headers = {"Authorization": "Bearer {token}", **recorded_headers}
        kept = {k: v for k, v in resp_headers.items() if k.lower() in ("content-type", "location")}
        run.exchanges.append(Exchange(
            op=step["op"],
            request={
                "method": method,
                "path": record_path,
                "query": dict(urllib.parse.parse_qsl(parsed.query, keep_blank_values=True))
                if not step.get("record_path") else {},
                "headers": recorded_headers,
                "body": sent_body,
            },
            response={"status": status, "headers": kept, "body": body},
        ))


def follow(body, run: Run, headers: dict) -> str:
    """Walks @odata.nextLink pages (unrecorded) and returns the final deltaLink."""
    while body and "@odata.nextLink" in body:
        status, resp_headers, raw = send("GET", body["@odata.nextLink"], headers, None)
        if status != 200:
            raise StepFailed(f"nextLink: HTTP {status}")
        body = decode_body(resp_headers, raw)
    link = (body or {}).get("@odata.deltaLink")
    if not link:
        raise StepFailed("no @odata.deltaLink at the end of the delta walk")
    return link


def wait_notification(step: dict, run: Run) -> None:
    if run.dry_run:
        print(f"    {step['op']}: wait for a notification")
        return
    deadline = time.time() + step.get("timeout", 120)
    while time.time() < deadline:
        try:
            item = run.notifications.get(timeout=max(1, deadline - time.time()))
        except queue.Empty:
            break
        if item["kind"] == "notification":
            run.exchanges.append(Exchange(step["op"], item["request"], item["response"], "outbound"))
            print(f"    {step['op']}: received")
            return
    raise StepFailed(f"{step['op']}: no notification within {step.get('timeout', 120)} s")


# ------------------------------------------------------------ notifications

def start_listener(port: int, run: Run) -> None:
    class Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self):  # noqa: N802 (stdlib name)
            parsed = urllib.parse.urlsplit(self.path)
            query = dict(urllib.parse.parse_qsl(parsed.query))
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length) if length else b""
            request = {
                "method": "POST",
                "path": "/notify",
                "query": query,
                "headers": {"Content-Type": self.headers.get("Content-Type", "")},
                "body": json.loads(raw) if raw and "json" in (self.headers.get("Content-Type") or "") else (raw.decode() or None),
            }
            if "validationToken" in query:
                token = query["validationToken"]
                self.send_response(200)
                self.send_header("Content-Type", "text/plain")
                self.end_headers()
                self.wfile.write(token.encode())
                run.exchanges.append(Exchange(
                    "subscriptions.validation", request,
                    {"status": 200, "headers": {"Content-Type": "text/plain"}, "body": token}, "outbound"))
                return
            self.send_response(202)
            self.end_headers()
            run.notifications.put({"kind": "notification", "request": request,
                                   "response": {"status": 202, "headers": {}, "body": None}})

        def log_message(self, *_):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"listening for notifications on 127.0.0.1:{port}")


# ------------------------------------------------------------------ scrubbing

def jwt_claims(token: str) -> dict:
    try:
        payload = token.split(".")[1]
        return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    except (IndexError, ValueError):
        return {}


def collect_ids(node, found: dict[str, str]) -> None:
    if isinstance(node, dict):
        for key, value in node.items():
            if isinstance(value, str) and ID_KEYS.match(key) and len(value) >= 8:
                # A Message-ID keeps its angle brackets: only what is inside them identifies anything.
                found.setdefault(value.strip("<>") if key == "internetMessageId" else value, key)
            collect_ids(value, found)
    elif isinstance(node, list):
        for item in node:
            collect_ids(item, found)


def build_replacements(run: Run, me: dict) -> list[tuple[str, str]]:
    pairs: dict[str, str] = {}
    claims = jwt_claims(run.token)
    for guid, fake in ((claims.get("tid"), "11111111-1111-1111-1111-111111111111"),
                       (claims.get("oid"), "22222222-2222-2222-2222-222222222222"),
                       (me.get("id"), "22222222-2222-2222-2222-222222222222")):
        if guid:
            pairs[guid] = fake
    for addr in {me.get("mail"), me.get("userPrincipalName")} - {None}:
        pairs[addr] = "me@example.com"
        pairs[addr.split("@")[1]] = "example.com"
    if run.colleague:
        pairs[run.colleague] = "colleague@example.com"
        pairs[run.colleague.split("@")[1]] = "example.com"
    if run.notification_url:
        pairs[run.notification_url] = FAKE_NOTIFY_URL

    ids: dict[str, str] = {}
    for ex in run.exchanges:
        collect_ids(ex.response["body"], ids)
        collect_ids(ex.request["body"], ids)
    counters: dict[str, int] = {}
    for value, key in ids.items():
        if value in pairs:
            continue
        counters[key] = counters.get(key, 0) + 1
        n = counters[key]
        pairs[value] = (f"00000000-0000-0000-0000-{n:012d}" if GUID.match(value)
                        else f"AAMk-{key}-{n:04d}=")

    out: list[tuple[str, str]] = []
    for real, fake in pairs.items():
        out.append((real, fake))
        quoted = urllib.parse.quote(real, safe="")
        if quoted != real:
            out.append((quoted, urllib.parse.quote(fake, safe="")))
    return sorted(out, key=lambda p: -len(p[0]))


@dataclass
class Names:
    """The mailbox owner's names. Unlike ids and addresses they are ordinary words ("Test User"), so a
    bare substring replace would corrupt "Users/…" or "@odata.type"; they are replaced only as a whole
    name-field value, or (the display name) as a whole phrase."""

    by_value: dict[str, str]
    phrase: re.Pattern | None

    @staticmethod
    def of(me: dict) -> "Names":
        fakes = {"displayName": "Me Example", "givenName": "Me", "surname": "Example"}
        by_value = {me[k]: fake for k, fake in fakes.items() if me.get(k)}
        display = me.get("displayName")
        phrase = re.compile(rf"(?<![\w]){re.escape(display)}(?![\w])") if display else None
        return Names(by_value, phrase)


NAME_KEYS = {"displayName", "givenName", "surname", "name"}

# What the id map cannot catch: Exchange ids of objects created in setup steps, or in another
# encoding (webLink's %2F/%2B form, uploadUrl's '-'/'_' form), and credentials Microsoft embeds in
# URLs (an upload session's authtoken is a JWT carrying the tenant and user ids). Placeholders
# ("AAMk-…") cannot match: a real id has 'A' straight after its four-character prefix.
# '/' and '+' only ever appear percent-encoded (webLink); a raw '/' is a path separator, which a match must stop at.
EXCHANGE_ID = re.compile(r"A[AQ][MQ]kA(?:[A-Za-z0-9_=-]|%2[BbFf]|%3[Dd]){20,}")
JWT = re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+")
AUTHTOKEN = re.compile(r"(authtoken=)[^&\"'\s]+", re.IGNORECASE)
# Graph's continuation tokens in @odata.nextLink/@odata.deltaLink: opaque, and they may encode mailbox state.
LINK_TOKEN = re.compile(r"(\$(?:skip|delta)token=)[^&\"'\s]+", re.IGNORECASE)


class OpaqueIds:
    """Maps each leftover Exchange id to one placeholder, whatever its encoding."""

    def __init__(self) -> None:
        self.map: dict[str, str] = {}

    def __call__(self, text: str) -> str:
        text = AUTHTOKEN.sub(r"\1{token}", text)
        text = LINK_TOKEN.sub(r"\1{token}", text)
        text = JWT.sub("{jwt}", text)
        return EXCHANGE_ID.sub(self._replace, text)

    def _replace(self, match: re.Match) -> str:
        key = urllib.parse.unquote(match.group(0)).replace("+", "-").replace("/", "_")
        if key not in self.map:
            self.map[key] = f"AAMk-ref-{len(self.map) + 1:04d}="
        return self.map[key]


def scrub(node, pairs: list[tuple[str, str]], names: Names, opaque: OpaqueIds, key: str | None = None):
    """Replaces in values only: keys are Graph's own vocabulary."""
    if isinstance(node, str):
        if key in NAME_KEYS and node in names.by_value:
            return names.by_value[node]
        for real, fake in pairs:
            if real in node:
                node = node.replace(real, fake)
        if names.phrase:
            node = names.phrase.sub("Me Example", node)
        return opaque(node)
    if isinstance(node, list):
        return [scrub(v, pairs, names, opaque, key) for v in node]
    if isinstance(node, dict):
        return {k: scrub(v, pairs, names, opaque, k) for k, v in node.items()}
    return node


def rescrub(folder: Path) -> int:
    """Applies the id/credential pass to fixtures already written (they hold no other originals)."""
    opaque = OpaqueIds()
    names = Names({}, None)
    changed = 0
    for path in sorted(folder.glob("*.json")):
        before = json.loads(path.read_text(encoding="utf-8"))
        after = scrub(before, [], names, opaque)
        if after != before:
            path.write_text(json.dumps(after, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            changed += 1
    return changed


def scrub_tokens(request: dict) -> dict:
    for key in list(request["query"]):
        if key in TOKEN_PARAMS:
            request["query"][key] = "{token}"
    return request


def write_fixtures(run: Run, me: dict, allow_leaks: bool, date: str) -> int:
    pairs = build_replacements(run, me)
    names = Names.of(me)
    opaque = OpaqueIds()
    sensitive = [real for real, _ in pairs if not real.startswith(("AAMk", "00000000"))
                 and len(real) >= 4 and not real.lower().endswith("example.com")]
    OUT.mkdir(exist_ok=True)
    leaks = 0
    for ex in run.exchanges:
        fixture = {
            "operationId": ex.op,
            "provenance": f"Captured from Microsoft Graph v1.0 (tenant capture {date}), scrubbed",
            **({"direction": "outbound"} if ex.direction == "outbound" else {}),
            "request": scrub_tokens(scrub(ex.request, pairs, names, opaque)),
            "response": scrub(ex.response, pairs, names, opaque),
        }
        text = json.dumps(fixture, indent=2, ensure_ascii=False) + "\n"
        found = [s for s in sensitive if s in text]
        if names.phrase and names.phrase.search(text):
            found.append(names.phrase.pattern)
        if found:
            leaks += 1
            print(f"  LEAK in {ex.op}: {found}", file=sys.stderr)
            if not allow_leaks:
                continue
        (OUT / f"{ex.op}.json").write_text(text, encoding="utf-8")
    return leaks


# ----------------------------------------------------------------------- main

def time_vars(run: Run) -> None:
    now = datetime.now(timezone.utc).replace(second=0, microsecond=0)
    day = (now + timedelta(days=1)).replace(hour=10, minute=0)
    fmt = "%Y-%m-%dT%H:%M:%S"
    run.vars.update({
        "now": now.strftime(fmt),
        "in30m": (now + timedelta(minutes=30)).strftime(fmt),
        "in1h": (now + timedelta(hours=1)).strftime(fmt),
        "in90m": (now + timedelta(minutes=90)).strftime(fmt),
        "in2h": (now + timedelta(hours=2)).strftime(fmt),
        "in1d": (now + timedelta(days=1)).strftime(fmt),
        "tomorrow10": day.strftime(fmt),
        "tomorrowDate": day.strftime("%Y-%m-%d"),
        "tomorrowWeekday": day.strftime("%A").lower(),
        "tomorrow1030": (day + timedelta(minutes=30)).strftime(fmt),
        "in4d": (day + timedelta(days=3)).strftime(fmt),
        "windowStart": now.strftime(fmt),
        "windowEnd": (now + timedelta(days=35)).strftime(fmt),
        "expiry1h": (now + timedelta(hours=1)).strftime(fmt) + "Z",
        "expiry2h": (now + timedelta(hours=2)).strftime(fmt) + "Z",
        "dueDate": (day + timedelta(days=2)).strftime(fmt),
        "run": hashlib.sha1(str(time.time()).encode()).hexdigest()[:6],
    })


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rescrub", action="store_true",
                    help="re-run the id/credential scrub over out/ (fixtures from an older version)")
    ap.add_argument("--phase", action="append",
                    help="phase number (11-16, 18, 19, 21) or 'all'; repeatable")
    ap.add_argument("--dry-run", action="store_true", help="print the requests without sending them")
    ap.add_argument("--notification-url", help="public https URL tunnelled to --listen (phase 16)")
    ap.add_argument("--listen", type=int, default=8765, help="local port for notifications (default 8765)")
    ap.add_argument("--colleague", help="another mailbox in the tenant (calendar sharing)")
    ap.add_argument("--photo", type=Path, help="JPEG for the contact photo (default: a 1x1 JPEG)")
    ap.add_argument("--allow-leaks", action="store_true", help="write fixtures even if the leak check fails")
    args = ap.parse_args()
    if args.rescrub:
        print(f"{rescrub(OUT)} fixture(s) changed in {OUT}")
        return 0
    if not args.phase:
        ap.error("--phase is required")

    phases = sorted(scenarios.PHASES) if "all" in args.phase else [int(p) for p in args.phase]
    unknown = [p for p in phases if p not in scenarios.PHASES]
    if unknown:
        ap.error(f"no scenarios for phase(s) {unknown}")

    token = os.environ.get("GRAPH_TOKEN", "")
    if not token and not args.dry_run:
        ap.error("set GRAPH_TOKEN (see tools/capture/README.md)")
    run = Run(token=token, dry_run=args.dry_run, notification_url=args.notification_url,
              colleague=args.colleague,
              photo=args.photo.read_bytes() if args.photo else base64.b64decode(scenarios.TINY_JPEG))
    time_vars(run)
    run.vars["clientState"] = CLIENT_STATE
    run.vars["notificationUrl"] = args.notification_url or FAKE_NOTIFY_URL
    run.vars["colleague"] = args.colleague or "colleague@example.com"

    me: dict = {}
    if not args.dry_run:
        scopes = set(jwt_claims(token).get("scp", "").split())
        missing = scenarios.SCOPES - scopes
        if missing:
            print(f"warning: token lacks {sorted(missing)}; some steps will fail", file=sys.stderr)
        status, headers, raw = send("GET", f"{GRAPH}/v1.0/me", {"Authorization": f"Bearer {token}"}, None)
        if status != 200:
            print(f"GET /me failed: {status} {raw[:300]!r}", file=sys.stderr)
            return 1
        me = json.loads(raw)
    run.vars["me.mail"] = me.get("mail") or me.get("userPrincipalName") or "me@example.com"

    if 16 in phases and args.notification_url and not args.dry_run:
        start_listener(args.listen, run)

    failed = 0
    base_vars = dict(run.vars)
    for phase in phases:
        for name, steps in scenarios.PHASES[phase]:
            print(f"phase {phase} / {name}")
            # Ids never carry over: a cleanup step must not delete another scenario's object.
            run.vars = dict(base_vars)
            main_steps = [s for s in steps if not s.get("always")]
            cleanup = [s for s in steps if s.get("always")]
            try:
                for step in main_steps:
                    run_step(step, run)
            except StepFailed as err:
                failed += 1
                print(f"  FAILED: {err}", file=sys.stderr)
            for step in cleanup:
                try:
                    run_step(step, run)
                except StepFailed as err:
                    # An unset id means the scenario stopped before creating that object.
                    if not str(err).startswith("unset variable"):
                        print(f"  cleanup failed: {err}", file=sys.stderr)

    if args.dry_run:
        return 0
    leaks = write_fixtures(run, me, args.allow_leaks, datetime.now(timezone.utc).strftime("%Y-%m-%d"))
    ops = sorted({ex.op for ex in run.exchanges})
    print(f"\n{len(ops)} operations recorded to {OUT}; {failed} scenario(s) failed; {leaks} leak(s)")
    return 1 if failed or leaks else 0


if __name__ == "__main__":
    sys.exit(main())
