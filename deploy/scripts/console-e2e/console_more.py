"""More live checks on the test stack, reusing console_e2e.py's helpers.

Usage: python3 -I console_more.py <deploy dir> <step>...
"""
import base64, hashlib, html, os, re, secrets, subprocess, sys, time, urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, "console_e2e.py")).read().replace("\nmain()\n", "\n")
deploy, steps = sys.argv[1], sys.argv[2:]
sys.argv = [sys.argv[0], deploy]
g = {"__name__": "e2e", "__file__": os.path.join(HERE, "console_e2e.py")}
exec(compile(src, "console_e2e.py", "exec"), g)
curl, token, expect, state, ENV, BASE, totp, form_action = (g[k] for k in ("curl", "token", "expect", "state", "ENV", "BASE", "totp", "form_action"))
save = lambda: __import__("json").dump(state, open(g["STATE"], "w"))


def sign_in(user):
    g["sign_in"](user)


def user_id(entity, email):
    s, u, body = curl(f"/console/entities/{entity}/users")
    m = re.search(r'href="/console/users/([^"]+)">[^<]*</a></td>\s*<td>' + re.escape(email) + "<", body)
    return m.group(1) if m else None


def post(url, fields):
    s, u, page = curl(url)
    fields = dict(fields, __RequestVerificationToken=token(page))
    return curl(url.split("?")[0] + ("?handler=" + fields.pop("_handler") if "_handler" in fields else ""), fields)


def stalwart(calls):
    script = os.path.join(HERE, "stalwart-call.sh")
    open(script, "w").write('#!/usr/bin/env bash\nsource "$1/scripts/lib.sh"\nUSING=\'"using":["urn:ietf:params:jmap:core","urn:stalwart:jmap"]\'\nstalwart_api "{${USING},\\"methodCalls\\":$2}"\n')
    out = subprocess.run(["bash", script, deploy, calls], capture_output=True, text=True, env=dict(os.environ, NIGHTWARE_CONNECT_ADDRESS="127.0.0.1")).stdout
    return __import__("json").loads(out)["methodResponses"]


def account(name):
    r = stalwart('[["x:Account/query",{"filter":{"name":"%s"}},"q"],["x:Account/get",{"#ids":{"resultOf":"q","name":"x:Account/query","path":"/ids"},"properties":["name","aliases","permissions"]},"g"]]' % name)
    return r[1][1]["list"]


for step in steps:
    print(f"--- {step}")
    if step == "signin-alice":
        sign_in("alice")
    elif step == "grant-bob-entity-admin":
        entity = state["default"]
        bob = user_id(entity, "bob@nightware.localhost")
        s, u, body = post(f"/console/entities/{entity}", {"_handler": "GrantAdmin", "userId": bob})
        expect("Administrator added" in body, "bob made entity admin of the default entity")
        state["bob"] = bob
        save()
    elif step == "signin-bob":
        sign_in("bob")
    elif step == "bob-scope":
        s, u, body = curl("/console/")
        expect("Nightware" in body and "Acme Test" not in body and "Beta Co" not in body, "bob's dashboard lists only his entity")
        expect("entity admin" in body and "Global admins" not in body, "shown as entity admin, no global menu")
        for path, want in ((f"/console/entities/{state['acme']}", 403), ("/console/admins", 403), (f"/console/entities/{state['acme']}/users", 403)):
            s, u, _ = curl(path, follow=False)
            expect(s == want, f"{path} -> {s} (want {want})")
        alice = user_id(state["default"], "alice@nightware.localhost")
        s, u, _ = curl(f"/console/users/{alice}", follow=False)
        expect(s == 404, f"bob cannot open the global admin alice's page ({s})")
        s, u, page = curl(f"/console/entities/{state['default']}/users")
        expect(s == 200 and "alice@nightware.localhost" in page, "bob lists his entity's users")
    elif step == "create-gina":
        entity = state["acme"]
        s, u, body = post(f"/console/entities/{entity}/users/new?handler=Apply", {"_handler": "Apply", "Form.FirstName": "Gina", "Form.LastName": "Kaur",
            "Form.LocalPart": "gina", "Form.Domain": "acme.test", "Form.Delivery": "TemporaryPassword"})
        pw = re.search(r"<code>([^<]+)</code>", body)
        expect(pw is not None, "gina created with a one-time password")
        state["gina_password"] = html.unescape(pw.group(1))
        state["gina"] = re.search(r'href="/console/users/([^"]+)">Open the user', body).group(1)
        save()
    elif step == "aliases":
        uid = state["gina"]
        s, u, body = post(f"/console/users/{uid}", {"_handler": "Aliases", "aliases": "gina.k@acme.test, g.kaur@acme.test"})
        expect("Aliases saved" in body, "aliases saved")
        a = account("gina")[0].get("aliases")
        print("     stalwart aliases:", a)
        names = sorted(v["name"] for v in (a.values() if isinstance(a, dict) else a))
        expect(names == ["g.kaur", "gina.k"], "both aliases in Stalwart")
        s, u, body = post(f"/console/users/{uid}", {"_handler": "Aliases", "aliases": "gina.k@acme.test"})
        a = account("gina")[0].get("aliases")
        names = sorted(v["name"] for v in (a.values() if isinstance(a, dict) else a))
        expect(names == ["gina.k"], f"one alias removed in Stalwart ({names})")
        s, u, body = post(f"/console/users/{uid}", {"_handler": "Aliases", "aliases": ""})
        a = account("gina")[0].get("aliases")
        expect(not a, f"all aliases cleared in Stalwart ({a})")
    elif step == "reinstate-frank":
        uid = state["frank"]
        s, u, body = post(f"/console/users/{uid}", {"_handler": "Reinstate"})
        expect("Reinstated" in body, "frank reinstated")
        print("     " + "; ".join(" ".join(x) for x in re.findall(r'<td>([^<]+)</td>\s*<td><span class="chip (\w+)">', body)))
        perms = account("frank")[0]["permissions"]
        expect(perms.get("@type") == "Inherit", f"frank's mail sign-in restored ({perms})")
    elif step == "reinstate-erin-recreate":
        uid = state["erin"]
        s, u, body = post(f"/console/users/{uid}", {"_handler": "Reinstate", "recreateMailbox": "true"})
        expect("Reinstated" in body, "erin reinstated")
        rows = re.findall(r'<td>([^<]+)</td>\s*<td><span class="chip (\w+)">', body)
        print("     " + "; ".join(" ".join(x) for x in rows))
        expect(("Mail (Stalwart)", "linked") in rows, "erin has a new, empty mailbox")
    elif step == "delete-dave-nextcloud":
        uid = state["dave"]
        url = f"/console/users/{uid}/delete/GroupwareData"
        s, u, page = curl(url)
        expect(s == 200 and "permanently deletes" in page, f"Nextcloud delete page ({s})")
        s, u, body = curl(url, {"__RequestVerificationToken": token(page), "confirmation": "dave@acme.test"})
        expect("Nextcloud user and their data were deleted" in body, "dave's Nextcloud data deleted")
        print("     " + "; ".join(" ".join(x) for x in re.findall(r'<td>([^<]+)</td>\s*<td><span class="chip (\w+)">', body)))
    elif step == "gina-signs-in":
        # NightMail's own client: auth code + PKCE, the shown one-time password, then Keycloak's forced change.
        g["JAR"] = os.path.join(HERE, "gina.jar")
        if os.path.exists(g["JAR"]):
            os.remove(g["JAR"])
        verifier = secrets.token_urlsafe(48)
        challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
        redirect = ENV.get("NIGHTMAIL_WEB_ORIGIN", "http://localhost:34572").strip('"') + "/callback.html"
        q = urllib.parse.urlencode({"client_id": "nightmail", "response_type": "code", "scope": "openid email profile", "redirect_uri": redirect,
            "code_challenge": challenge, "code_challenge_method": "S256", "state": "s"})
        s, u, page = curl(f"{BASE}/auth/realms/nightware/protocol/openid-connect/auth?{q}")
        s, u, page = curl(form_action(page, "kc-form-login"), {"username": "gina", "password": state["gina_password"]})
        expect("password-new" in page, "Keycloak forces a new password at first sign-in")
        new = "Gina-Own-Pass-" + secrets.token_hex(4) + "!"
        args = ["curl", "-sS", "--cacert", g["CA"], "--resolve", f"nightware.localhost:{g['PORT']}:127.0.0.1", "-c", g["JAR"], "-b", g["JAR"],
                "-o", "/dev/null", "-w", "%{redirect_url}", "--data-raw", urllib.parse.urlencode({"password-new": new, "password-confirm": new}), form_action(page, "kc-passwd-update-form")]
        location = subprocess.run(args, capture_output=True, text=True).stdout
        code = urllib.parse.parse_qs(urllib.parse.urlparse(location).query).get("code", [None])[0]
        expect(code is not None, f"redirected back to NightMail with a code ({location[:60]})")
        out = subprocess.run(["curl", "-sS", "--cacert", g["CA"], "--resolve", f"nightware.localhost:{g['PORT']}:127.0.0.1", "-d", "grant_type=authorization_code",
            "-d", f"code={code}", "-d", f"redirect_uri={redirect}", "-d", "client_id=nightmail", "-d", f"code_verifier={verifier}",
            f"{BASE}/auth/realms/nightware/protocol/openid-connect/token"], capture_output=True, text=True).stdout
        tok = __import__("json").loads(out)
        expect("access_token" in tok, "gina has NightMail tokens")
        claims = __import__("json").loads(base64.urlsafe_b64decode(tok["access_token"].split(".")[1] + "=="))
        print("     token email:", claims.get("email"), " preferred_username:", claims.get("preferred_username"))
        state["gina_token"] = tok["access_token"]
        save()
    elif step == "gina-reads-mail":
        # Through the Graph façade if it runs; else straight at Stalwart's JMAP with her token.
        tok = state["gina_token"]
        r = subprocess.run(["curl", "-sS", "--cacert", g["CA"], "--resolve", f"nightware.localhost:{g['PORT']}:127.0.0.1", "-H", "@-",
            f"{BASE}/jmap/session"], input=f"Authorization: Bearer {tok}\n", capture_output=True, text=True).stdout
        print("     jmap session username:", (__import__("json").loads(r) if r.startswith("{") else {}).get("username", r[:120]))
        n = len([a for a in account("gina")])
        expect(n == 1, f"still exactly one Stalwart account for gina ({n})")
    elif step == "gina-graph":
        # Her NightMail password is now the one she chose; sign in again for a fresh token.
        tok = state["gina_token"]
        def graph(path):
            return subprocess.run(["curl", "-sS", "--cacert", g["CA"], "--resolve", f"nightware.localhost:{g['PORT']}:127.0.0.1", "-H", "@-",
                "-w", "\n%{http_code}", f"{BASE}/graph/v1.0{path}"], input=f"Authorization: Bearer {tok}\n", capture_output=True, text=True).stdout
        out = graph("/me")
        body, status = out.rsplit("\n", 1)
        print("     /me ->", status, body[:160])
        expect(status == "200" and "gina@acme.test" in body, "the façade knows gina")
        out = graph("/me/mailFolders/inbox")
        body, status = out.rsplit("\n", 1)
        print("     inbox ->", status, body[:120])
        expect(status == "200", "gina's inbox opens through the façade")
        n = len(account("gina"))
        expect(n == 1, f"still exactly one Stalwart account for gina ({n})")
    else:
        raise SystemExit(f"unknown step {step}")
