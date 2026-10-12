import copy
import json
from pathlib import Path
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

import inventory

USER = "1e6730cccb154ed28f5b278109a83bb3"
MOVIE = "e32d6359-2358-b57c-d0a7-35c81eeba24e"
AUTH = {"AccessToken": "private-session-token", "User": {"Id": USER, "Policy": {"IsAdministrator": True}}}
PLUGIN = [{"Id": "33e6d249-648f-44cd-a9ce-497be06c08df", "Version": "12.1.0.3", "Status": "Active"}]
ITEM = {"Id": MOVIE, "Name": "Тестовый фильм", "Type": "Movie", "ProviderIds": {"kinopoisk": "301", "Imdb": "tt0111161"},
        "Overview": "Тестовое описание", "ImageTags": {"Primary": "tag"}, "MediaSourceCount": 1,
        "RemoteTrailers": [{"Url": "https://widgets.kinopoisk.ru/discovery/film/301/trailer/1?secret=private-media-token"}]}
STATUS = {"KinopoiskId": 301, "MovieId": MOVIE, "State": "ready-local-cache", "Registered": True,
          "CacheReady": True, "CachePath": "/private/cache/trailer.mp4", "CacheEntryBytes": 100000}


class FakeApi:
    identity = "test-identity"
    authorization = identity

    def __init__(self, items=None):
        self.items = [copy.deepcopy(ITEM)] if items is None else items
        self.auth = copy.deepcopy(AUTH)
        self.status = copy.deepcopy(STATUS)
        self.calls = []
        self.fail = None

    def request(self, route, method="GET", payload=None):
        self.calls.append((method, route))
        if self.fail and route.startswith(self.fail):
            raise RuntimeError("route failed")
        if route == "/System/Info/Public":
            return {"Version": "12.1.0"}
        if route == "/Users/AuthenticateByName":
            return self.auth
        if route == "/Sessions/Logout":
            return None
        if route == "/Plugins":
            return PLUGIN
        if route == "/KinopoiskPlayback/capabilities":
            return {"NativeTrailerBridge": {}}
        if route == "/KinopoiskPlayback/cache/statistics":
            return {"Cache": {"FileCount": 1, "CurrentBytes": 100000}, "Warmup": {"ActiveCount": 0}}
        if route.startswith("/Items?"):
            index = int(parse_qs(urlsplit(route).query)["StartIndex"][0])
            return {"Items": self.items[index:index+100], "TotalRecordCount": len(self.items)}
        if route.startswith("/KinopoiskPlayback/native-bridge/"):
            return self.status
        raise AssertionError(route)


class InventoryTests(unittest.TestCase):
    def test_read_only_report_omitted_secrets_and_closed_session(self):
        api = FakeApi()
        result = inventory.session_inventory(api, "test", "private-password")
        self.assertEqual(result["inventory"], "PASS")
        self.assertEqual(result["movies_with_kinopoisk_id"], 1)
        self.assertEqual(result["items"][0]["native_bridge"]["state"], "ready-local-cache")
        self.assertTrue(result["items"][0]["primary_image_present"])
        rendered = json.dumps(result)
        for secret in ("private-session-token", "private-password", "private-media-token", "/private/cache"):
            self.assertNotIn(secret, rendered)
        self.assertEqual(api.calls[-1], ("POST", "/Sessions/Logout"))
        self.assertEqual(api.authorization, api.identity)
        writes = [route for method, route in api.calls if method != "GET"]
        self.assertEqual(writes, ["/Users/AuthenticateByName", "/Sessions/Logout"])
        self.assertFalse(any(route.startswith("/Items/") for method, route in api.calls))

    def test_movie_without_id_and_series_did_not_request_bridge(self):
        movie = copy.deepcopy(ITEM)
        movie["ProviderIds"] = {}
        series = copy.deepcopy(ITEM)
        series.update(Id="9c4cc1e3461b4b9aad38841d3b6787bf", Type="Series")
        api = FakeApi([movie, series])
        result = inventory.session_inventory(api, "test", "test")
        self.assertEqual(result["visible_movies_and_series"], 2)
        self.assertEqual(result["movies_with_kinopoisk_id"], 0)
        self.assertFalse(any("/native-bridge/" in route for method, route in api.calls))

    def test_empty_library_was_accepted(self):
        result = inventory.session_inventory(FakeApi([]), "test", "test")
        self.assertEqual(result["visible_movies_and_series"], 0)
        self.assertEqual(result["functional_acceptance"], "PENDING")

    def test_pagination_returned_all_objects(self):
        items = []
        for number in range(101):
            item = copy.deepcopy(ITEM)
            item["Id"] = f"{number+1:032x}"
            item["ProviderIds"] = {}
            items.append(item)
        api = FakeApi(items)
        result = inventory.session_inventory(api, "test", "test")
        self.assertEqual(result["visible_movies_and_series"], 101)
        self.assertEqual(sum(route.startswith("/Items?") for method, route in api.calls), 2)

    def test_failed_route_closed_session(self):
        api = FakeApi()
        api.fail = "/KinopoiskPlayback/cache/statistics"
        with self.assertRaises(RuntimeError):
            inventory.session_inventory(api, "test", "test")
        self.assertEqual(api.calls[-1], ("POST", "/Sessions/Logout"))
        self.assertEqual(api.authorization, api.identity)

    def test_wrong_bridge_owner_was_rejected(self):
        api = FakeApi()
        api.status["MovieId"] = USER
        with self.assertRaisesRegex(RuntimeError, "не совпало"):
            inventory.session_inventory(api, "test", "test")
        self.assertEqual(api.calls[-1], ("POST", "/Sessions/Logout"))

    def test_duplicate_page_items_were_rejected(self):
        api = FakeApi([ITEM, ITEM])
        with self.assertRaisesRegex(RuntimeError, "изменился"):
            inventory.session_inventory(api, "test", "test")
        self.assertEqual(api.calls[-1], ("POST", "/Sessions/Logout"))

    def test_non_admin_session_was_closed(self):
        api = FakeApi()
        api.auth["User"]["Policy"]["IsAdministrator"] = False
        with self.assertRaisesRegex(RuntimeError, "администратор"):
            inventory.session_inventory(api, "test", "test")
        self.assertEqual(api.calls[-1], ("POST", "/Sessions/Logout"))

    def test_local_url_and_read_only_methods(self):
        for url in ("https://kinopoiskapiunofficial.tech", "http://user:password@127.0.0.1:8096",
                    "http://127.0.0.1:8096?secret=token", "http://127.0.0.1:wrong"):
            with self.subTest(url=url), self.assertRaises(RuntimeError):
                inventory.LocalApi(url)
        api = inventory.LocalApi("http://127.0.0.1:8096/jellyfin/")
        self.assertEqual(api.url, "http://127.0.0.1:8096/jellyfin")
        with self.assertRaises(RuntimeError):
            api.request("/Library/Refresh", "POST", {})

    def test_http_transport_with_base_url_and_modern_authorization(self):
        fake = FakeApi()
        headers = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.respond()

            def do_POST(self):
                self.respond()

            def respond(self):
                headers.append(dict(self.headers))
                payload = None
                if self.command == "POST":
                    payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                route = self.path.removeprefix("/jellyfin")
                result = fake.request(route, self.command, payload)
                body = b"" if result is None else json.dumps(result).encode()
                self.send_response(204 if result is None else 200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            api = inventory.LocalApi(f"http://127.0.0.1:{server.server_port}/jellyfin")
            result = inventory.session_inventory(api, "test", "test")
            self.assertEqual(result["inventory"], "PASS")
            self.assertIn('Token="private-session-token"', headers[2]["Authorization"])
            self.assertTrue(headers[2]["Authorization"].startswith("MediaBrowser "))
            self.assertNotIn("X-Emby-Token", headers[2])
        finally:
            server.shutdown()
            thread.join(timeout=3)
            server.server_close()


if __name__ == "__main__":
    unittest.main()
