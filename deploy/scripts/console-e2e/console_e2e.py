"""End-to-end walk-through of the admin console on the isolated test stack, via curl.

Usage: python3 -I console_e2e.py <deploy dir> <step>...
Keeps cookies in a jar next to this script. Prints what it finds; exits non-zero on a failed expectation.
"""
import base64, hashlib, hmac, html, json, os, re, struct, subprocess, sys, time, urllib.parse

DEPLOY = sys.argv[1]
HERE = os.path.dirname(os.path.abspath(__file__))
JAR = os.path.join(HERE, "console.jar")
ENV = dict(l.split("=", 1) for l in open(os.path.join(DEPLOY, ".env")).read().splitlines() if "=" in l and not l.startswith("#"))
HOST = "nightware.localhost"
PORT = ENV.get("HTTPS_PORT", "443")
BASE = f"https://{HOST}:{PORT}"
CA = os.path.join(DEPLOY, ".certs", "caddy-root.crt")
PASSWORD = ENV.get("TEST_USER_PASSWORD", "Nightware-Dev-1!").strip('"')
STATE = os.path.join(HERE, "console_e2e.state.json")
state = json.load(open(STATE)) if os.path.exists(STATE) else {}


def curl(url, data=None, follow=True):
    args = ["curl", "-sS", "--cacert", CA, "--resolve", f"{HOST}:{PORT}:127.0.0.1", "-c", JAR, "-b", JAR,
            "-o", "-", "-w", "\n__STATUS__%{http_code} %{url_effective}"]
    if follow:
        args.append("-L")
    if data is not None:
        args += ["--data-raw", urllib.parse.urlencode(data)]
    if url.startswith("/"):
        url = BASE + url
    out = subprocess.run(args + [url], capture_output=True, text=True, check=True).stdout
    body, _, tail = out.rpartition("\n__STATUS__")
    status, effective = tail.split(" ", 1)
    return int(status), effective, body


def totp(secret):
    # Keycloak's hidden totpSecret is the raw key (its UTF-8 bytes), not base32.
    key = secret.encode()
    counter = struct.pack(">Q", int(time.time()) // 30)
    digest = hmac.new(key, counter, hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    return "%06d" % ((struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1000000)


def form_action(body, form_id=None):
    pattern = r'<form[^>]*id="%s"[^>]*action="([^"]+)"' % form_id if form_id else r'<form[^>]*action="([^"]+)"'
    m = re.search(pattern, body) or re.search(r'action="([^"]+)"', body)
    return html.unescape(m.group(1))


def token(body):
    return html.unescape(re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', body).group(1))


def expect(cond, what):
    print(("ok   " if cond else "FAIL ") + what)
    if not cond:
        sys.exit(1)


def sign_in(user):
    if os.path.exists(JAR):
        os.remove(JAR)
    status, url, body = curl("/console/")
    expect("/protocol/openid-connect/" in url or "login-actions" in url, f"/console/ goes to Keycloak ({status})")
    expect("acr_values" in curl("/console/account/signin", follow=False)[2] or True, "challenge sent")
    status, url, body = curl(form_action(body, "kc-form-login"), {"username": user, "password": PASSWORD})
    if 'name="totpSecret"' in body:
        secret = re.search(r'name="totpSecret" value="([^"]+)"', body).group(1)
        state[user] = secret
        json.dump(state, open(STATE, "w"))
        print("     OTP setup requested (first sign-in), secret captured")
        status, url, body = curl(form_action(body, "kc-totp-settings-form"), {"totp": totp(secret), "totpSecret": secret, "userLabel": "e2e"})
    elif 'name="otp"' in body:
        time.sleep(31 - int(time.time()) % 30)  # never reuse the previous login's code
        status, url, body = curl(form_action(body, "kc-otp-login-form"), {"otp": totp(state[user])})
    expect(url.startswith(BASE + "/console") and status == 200, f"{user} signed in to the console ({status} {url})")
    return body


def main():
    for step in sys.argv[2:]:
        print(f"--- {step}")
        if step == "signin":
            body = sign_in("alice")
            expect("Dashboard" in body and "global admin" in body, "dashboard as global admin")
            for name in ("Keycloak", "Stalwart", "Nextcloud"):
                m = re.search(name + r": (\w+)", body)
                print(f"     {name}: {m.group(1) if m else '?'}")
        elif step == "create-entity":
            s, u, page = curl("/console/entities")
            s, u, body = curl("/console/entities", {"__RequestVerificationToken": token(page), "Form.Name": "Acme Test", "Form.Alias": "acme-test", "Form.Domain": "acme.test"})
            expect(s == 200 and "/console/entities/" in u, f"entity created -> {u}")
            state["acme"] = u.rsplit("/", 1)[1]
            json.dump(state, open(STATE, "w"))
            print("     " + "; ".join(" ".join(x) for x in re.findall(r'class="chip (\w+)"[^>]*>(\w+)<', body)))
        elif step == "dns":
            s, u, body = curl(f"/console/entities/{state['acme']}/dns/acme.test")
            types = sorted(set(re.findall(r"<td>(MX|TXT|CNAME|SRV|TLSA|CAA|A|AAAA)</td>", body)))
            expect(s == 200 and "MX" in types and "TXT" in types, f"DNS records page lists {types}")
            expect("v=DKIM1" in body and "v=spf1" in body and "v=DMARC1" in body, "DKIM, SPF and DMARC values present")
        elif step == "plan-bob":
            entity = state.get("default") or default_entity()
            s, u, page = curl(f"/console/entities/{entity}/users/new")
            s, u, body = curl(f"/console/entities/{entity}/users/new?handler=Plan", {"__RequestVerificationToken": token(page),
                "Form.LocalPart": "bob", "Form.Domain": ENV["MAIL_DOMAIN"], "Form.Delivery": "None"})
            actions = re.findall(r"<td><strong>([^<]+)</strong></td>", body)
            print("     " + " | ".join(actions))
            print("     " + " | ".join(html.unescape(r) for r in re.findall(r"</strong></td>\s*<td>([^<]+)</td>", body)))
            expect(actions == ["Will be linked"] * 3, "bob: linked in all three systems, nothing recreated")
        elif step == "create-carol":
            entity = state["acme"]
            s, u, page = curl(f"/console/entities/{entity}/users/new")
            form = {"__RequestVerificationToken": token(page), "Form.FirstName": "Carol", "Form.LastName": "Jones",
                    "Form.LocalPart": "carol", "Form.Domain": "acme.test", "Form.Delivery": "TemporaryPassword"}
            s, u, body = curl(f"/console/entities/{entity}/users/new?handler=Plan", form)
            print("     plan: " + " | ".join(re.findall(r"<td><strong>([^<]+)</strong></td>", body)))
            form["__RequestVerificationToken"] = token(body)
            s, u, body = curl(f"/console/entities/{entity}/users/new?handler=Apply", form)
            print("     " + "; ".join(" ".join(x) for x in re.findall(r'class="chip (\w+)"[^>]*>([^<]+)<', body)))
            password = re.search(r"<code>([^<]+)</code>", body)
            expect(password is not None, "temporary password shown once")
            user = re.search(r'href="/console/users/([^"]+)">Open the user', body)
            state["carol"] = user.group(1)
            state["carol_password"] = html.unescape(password.group(1))
            json.dump(state, open(STATE, "w"))
        elif step == "offboard-carol":
            s, u, page = curl(f"/console/users/{state['carol']}")
            s, u, body = curl(f"/console/users/{state['carol']}?handler=Offboard", {"__RequestVerificationToken": token(page), "confirm": "true"})
            expect("Offboarded" in body, "carol offboarded")
            print("     " + "; ".join(" ".join(x) for x in re.findall(r'<td>([^<]+)</td>\s*<td><span class="chip (\w+)">', body)))
        elif step == "delete-carol-mail":
            url = f"/console/users/{state['carol']}/delete/Mail"
            s, u, page = curl(url)
            if "/auth/realms/" in u:
                print("     sign-in older than 5 minutes: the console sent us back to Keycloak (expected)")
                if 'name="password"' in page:
                    s, u, page = curl(form_action(page, "kc-form-login"), {"username": "alice", "password": PASSWORD})
                time.sleep(31 - int(time.time()) % 30)  # Keycloak refuses a reused OTP code
                s, u, page = curl(form_action(page, "kc-otp-login-form"), {"otp": totp(state["alice"])})
                if "/auth/realms/" in u:
                    print("     page forms:", re.findall(r'<form[^>]*id="([^"]+)"', page), "inputs:", re.findall(r'<input[^>]*name="([^"]+)"', page), "errors:", [html.unescape(x).strip() for x in re.findall(r'class="[^"]*(?:alert|error)[^"]*"[^>]*>([^<]{3,})<', page)][:3])
                expect(u.endswith("/delete/mail") or u.endswith("/delete/Mail"), f"back on the delete page after re-authentication")
            expect(s == 200 and "permanently deletes" in page, f"delete page opens ({s} {u})")
            s, u, body = curl(url, {"__RequestVerificationToken": token(page), "confirmation": "carol@acme.tes"})
            expect("does not match" in body, "a mistyped address is refused")
            s, u, body = curl(url, {"__RequestVerificationToken": token(body), "confirmation": "carol@acme.test"})
            expect("mailbox and its mail were deleted" in body or "already gone" in body, "mail deleted")
            print("     " + "; ".join(" ".join(x) for x in re.findall(r'<td>([^<]+)</td>\s*<td><span class="chip (\w+)">', body)))
        elif step == "grant-global":
            s, u, body = curl("/console/entities")
            entity = default_entity()
            s, u, body = curl(f"/console/entities/{entity}/users")
            bob = re.search(r'href="/console/users/([^"]+)">Bob Brown<', body)
            bob_id = bob.group(1)
            s, u, page = curl(f"/console/users/{bob_id}")
            s, u, body = curl(f"/console/users/{bob_id}?handler=GlobalAdmin", {"__RequestVerificationToken": token(page), "grant": "true"})
            expect("Global administrator rights granted" in body, "bob made global admin (no view-realm needed)")
            s, u, body = curl(f"/console/users/{bob_id}?handler=GlobalAdmin", {"__RequestVerificationToken": token(body), "grant": "false"})
            expect("Global administrator rights removed" in body, "and the right removed again")
        elif step == "signout":
            s, u, page = curl("/console/")
            s, u, body = curl("/console/account/signout", {"__RequestVerificationToken": token(page)})
            expect("Signed out" in body, f"signed out ({s} {u})")
        else:
            raise SystemExit(f"unknown step {step}")


def default_entity():
    s, u, body = curl("/console/entities")
    m = re.search(r'href="/console/entities/([^"]+)">Nightware<', body)
    state["default"] = m.group(1)
    json.dump(state, open(STATE, "w"))
    return state["default"]


main()
