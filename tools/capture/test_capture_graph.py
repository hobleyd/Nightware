"""Offline tests for the scrubber and path reader: python3 -m unittest tools/capture/test_capture_graph.py"""

import base64
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import capture_graph as c  # noqa: E402

ME = {
    "id": "bbbbbbbb-1111-2222-3333-444444444444",
    "mail": "test.user@contoso.onmicrosoft.com",
    "userPrincipalName": "test.user@contoso.onmicrosoft.com",
    "displayName": "Test User", "givenName": "Test", "surname": "User",
}


def token(claims: dict) -> str:
    return "x." + base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("=") + ".y"


class ScrubTests(unittest.TestCase):
    def setUp(self):
        self.run = c.Run(token=token({"tid": "aaaaaaaa-1111-2222-3333-444444444444", "oid": ME["id"]}),
                         dry_run=False, notification_url="https://abc.trycloudflare.com/notify",
                         colleague=None, photo=b"")
        self.out = tempfile.TemporaryDirectory()
        c.OUT = Path(self.out.name)

    def tearDown(self):
        self.out.cleanup()

    def fixture(self, op: str) -> dict:
        return json.loads((c.OUT / f"{op}.json").read_text())

    def test_ids_addresses_tokens_and_tunnel_are_replaced_consistently(self):
        gid = "AAMkADk0ZTQ5NmM5LWE3ZTctNGRmMS1hZjQ2LTQ3NjM2ZjY3NTZkMgBGAAAAAAB=="
        self.run.exchanges.append(c.Exchange(
            "x",
            {"method": "GET", "path": f"/v1.0/me/messages/{gid}", "query": {"$deltatoken": "abc"}, "headers": {}, "body": None},
            {"status": 200, "headers": {}, "body": {
                "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#users('test.user%40contoso.onmicrosoft.com')/x",
                "id": gid, "notificationUrl": "https://abc.trycloudflare.com/notify",
                "tenantId": "aaaaaaaa-1111-2222-3333-444444444444"}}))

        self.assertEqual(c.write_fixtures(self.run, ME, False, "2026-11-20"), 0)

        f = self.fixture("x")
        self.assertEqual(f["request"]["path"], "/v1.0/me/messages/" + f["response"]["body"]["id"])
        self.assertEqual(f["request"]["query"]["$deltatoken"], "{token}")
        body = f["response"]["body"]
        self.assertIn("users('me%40example.com')", body["@odata.context"])
        self.assertEqual(body["notificationUrl"], c.FAKE_NOTIFY_URL)
        self.assertEqual(body["tenantId"], "11111111-1111-1111-1111-111111111111")

    def test_names_are_replaced_as_names_not_as_substrings(self):
        self.run.exchanges.append(c.Exchange(
            "subscriptions.notification",
            {"method": "POST", "path": "/notify", "query": {}, "headers": {}, "body": {"value": [{
                "resource": f"Users/{ME['id']}/Messages/AAMkXYZ123456",
                "resourceData": {"@odata.type": "#Microsoft.Graph.Message", "id": "AAMkXYZ123456"}}]}},
            {"status": 202, "headers": {}, "body": None}, "outbound"))
        self.run.exchanges.append(c.Exchange(
            "me", {"method": "GET", "path": "/v1.0/me", "query": {}, "headers": {}, "body": None},
            {"status": 200, "headers": {}, "body": {
                "displayName": "Test User", "givenName": "Test", "surname": "User", "jobTitle": "Test",
                "from": {"emailAddress": {"name": "Test User", "address": ME["mail"]}},
                "bodyPreview": "Hi Test User, this is a Test of User settings"}}))

        self.assertEqual(c.write_fixtures(self.run, ME, False, "2026-11-20"), 0)

        note = self.fixture("subscriptions.notification")["request"]["body"]["value"][0]
        self.assertTrue(note["resource"].startswith("Users/22222222-2222-2222-2222-222222222222/Messages/AAMk-id-"))
        self.assertEqual(note["resourceData"]["@odata.type"], "#Microsoft.Graph.Message")
        body = self.fixture("me")["response"]["body"]
        self.assertEqual((body["displayName"], body["givenName"], body["surname"]), ("Me Example", "Me", "Example"))
        self.assertEqual(body["from"]["emailAddress"], {"name": "Me Example", "address": "me@example.com"})
        self.assertEqual(body["bodyPreview"], "Hi Me Example, this is a Test of User settings")
        self.assertEqual(body["jobTitle"], "Test")

    def test_the_display_name_is_replaced_as_a_phrase_in_any_value(self):
        self.run.exchanges.append(c.Exchange(
            "note", {"method": "GET", "path": "/v1.0/me", "query": {}, "headers": {}, "body": None},
            {"status": 200, "headers": {}, "body": {"note": "Ask Test User", "other": "Test Users"}}))

        self.assertEqual(c.write_fixtures(self.run, ME, False, "2026-11-20"), 0)

        body = self.fixture("note")["response"]["body"]
        self.assertEqual(body["note"], "Ask Me Example")
        self.assertEqual(body["other"], "Test Users")


class PickTests(unittest.TestCase):
    def test_reads_dotted_indexed_and_odata_paths(self):
        self.assertEqual(c.pick({"value": [{"id": 1}]}, "value[0].id"), 1)
        self.assertEqual(c.pick({"@odata.deltaLink": "l"}, "@odata.deltaLink"), "l")
        self.assertIsNone(c.pick({"value": []}, "value[0].id"))


if __name__ == "__main__":
    unittest.main()
