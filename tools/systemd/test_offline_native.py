import copy
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit
import xml.etree.ElementTree as ET

import inventory
import offline_native as native

MOVIE = "e32d63592358b57cd0a735c81eeba24e"
LIBRARY_ID = "24f017e68d5348b9b50aae8dd37b3b07"
TRAILER = "dc854d8d61ca4a5eafa96a14c49c0e2e"
USER = "1e6730cccb154ed28f5b278109a83bb3"
EXISTING = {"Id": "9c4cc1e3461b4b9aad38841d3b6787bf", "Type": "Movie", "Name": "Исходный фильм",
            "ProviderIds": {"Imdb": "tt0000001"}, "Overview": "Исходное описание"}


class FakeApi:
    identity = "test-client"
    authorization = identity

    def __init__(self, context):
        self.context = context
        self.owned_ids = set()
        self.folders = []
        self.movie = None
        self.existing = copy.deepcopy(EXISTING)
        self.calls = []
        self.range_count = 0
        self.damage_range = False

    def request(self, route, method="GET", payload=None):
        self.calls.append((method, route))
        parsed = urlsplit(route)
        params = parse_qs(parsed.query)
        path = parsed.path
        if path == "/Users/AuthenticateByName":
            return {"AccessToken": "private-test-token", "User": {"Id": USER, "Policy": {"IsAdministrator": True}}}
        if path == "/Sessions/Logout":
            return None
        if path == "/KinopoiskPlayback/capabilities":
            return {"NativeTrailerBridge": {"LocalTrailerCache": True}}
        if path == "/Library/VirtualFolders":
            if method == "POST":
                locations = [p["Path"] for p in payload["LibraryOptions"]["PathInfos"]]
                self.folders = [{"Name": native.LIBRARY, "Locations": locations, "ItemId": LIBRARY_ID}]
                options = payload["LibraryOptions"]["TypeOptions"][0]
                assert not options["MetadataFetchers"] and not options["ImageFetchers"]
                return None
            if method == "DELETE":
                self.folders = []
                self.movie = None
                return None
            return self.folders
        if path.endswith("/Refresh"):
            if LIBRARY_ID in path:
                nfo = native.state_root(self.context) / "media/fixture/offline-kinopoisk.nfo"
                xml = ET.parse(nfo).getroot()
                self.movie = {"Id": MOVIE, "Name": xml.findtext("title"), "Type": "Movie",
                              "ProviderIds": {"kinopoisk": xml.findtext("uniqueid")},
                              "DateLastRefreshed": "2026-01-01T00:00:00Z"}
            else:
                assert MOVIE in path
                self.movie["DateLastRefreshed"] = "2026-01-01T00:00:01Z"
            return None
        if path == "/Items":
            items = [] if self.movie is None else [self.movie]
            if "ParentId" not in params:
                items = [self.existing] + items
            return {"Items": items, "TotalRecordCount": len(items)}
        if path == f"/Items/{MOVIE}":
            return self.movie
        if path.startswith("/KinopoiskPlayback/native-bridge/"):
            return {"State": "ready-local-cache", "MovieId": MOVIE, "TrailerId": TRAILER, "CacheReady": True}
        if path == f"/Items/{MOVIE}/LocalTrailers":
            return [{"Id": TRAILER}]
        if path == f"/Items/{TRAILER}/PlaybackInfo":
            assert payload["EnableTranscoding"] is False
            return {"MediaSources": [{"Id": TRAILER, "SupportsDirectPlay": True,
                                     "MediaStreams": [{"Type": "Video", "Codec": "h264"}, {"Type": "Audio", "Codec": "aac"}]}]}
        raise AssertionError(route)

    def read_range(self, trailer_id, start, end):
        self.range_count += 1
        path = Path(self.context["data"]) / f"plugins/Jellyfin.Plugin.Kinopoisk/cache/trailers/kinopoisk-{native.KP_ID}.mp4"
        body = path.read_bytes()[start:end + 1]
        if self.damage_range:
            body = b"wrong"
        return body, f"bytes {start}-{end}/{path.stat().st_size}"


class NativeFixtureTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.context = {"data": self.temporary.name, "uid": os.getuid(), "gid": os.getgid()}
        self.cache = Path(self.temporary.name) / "plugins/Jellyfin.Plugin.Kinopoisk/cache/trailers"
        self.cache.mkdir(parents=True)
        self.api = FakeApi(self.context)

    def test_generated_mp4_registration_refresh_ranges_and_cleanup(self):
        state = native.prepare(self.api, self.context, USER)
        result = native.verify(self.api, self.context, state, USER)
        self.assertEqual(result["http_range"], "PASS")
        self.assertEqual(state["movie_id"], MOVIE)
        self.assertEqual(state["trailer_id"], TRAILER)
        self.assertEqual(native.refresh_fixture(self.api, self.context, state, USER), "PASS")
        self.assertEqual(self.api.range_count, 4)
        loaded = native.read_state(self.context)
        self.assertEqual(loaded["trailer_id"], TRAILER)
        metadata = json.loads((self.cache / f"kinopoisk-{native.KP_ID}.json").read_text())
        self.assertEqual(metadata["videoCodec"], "h264")
        self.assertEqual(metadata["audioCodec"], "aac")
        self.assertEqual(metadata["audioChannels"], 2)
        native.cleanup(self.api, self.context, loaded)
        self.assertFalse(native.state_root(self.context).exists())
        self.assertFalse((self.cache / f"kinopoisk-{native.KP_ID}.mp4").exists())
        self.assertEqual(self.api.existing, EXISTING)
        for method, route in self.api.calls:
            if method == "POST" and "/Items/" in route:
                self.assertTrue(any(item_id in route for item_id in (LIBRARY_ID, MOVIE, TRAILER)))

    def test_preexisting_cache_was_preserved(self):
        path = self.cache / f"kinopoisk-{native.KP_ID}.mp4"
        path.write_bytes(b"original")
        with self.assertRaisesRegex(RuntimeError, "занят в кэше"):
            native.prepare(self.api, self.context, USER)
        self.assertEqual(path.read_bytes(), b"original")
        self.assertFalse(native.state_root(self.context).exists())

    def test_preexisting_library_was_preserved(self):
        self.api.folders = [{"Name": native.LIBRARY, "Locations": ["/original"], "ItemId": LIBRARY_ID}]
        with self.assertRaisesRegex(RuntimeError, "уже существует"):
            native.prepare(self.api, self.context, USER)
        self.assertEqual(self.api.folders[0]["Locations"], ["/original"])

    def test_read_state_rejected_foreign_cache_path(self):
        root = native.state_root(self.context)
        root.mkdir()
        native.write_state(root, {"schema": 1, "library": native.LIBRARY, "kp_id": native.KP_ID, "cache_dir": "/foreign"})
        with self.assertRaisesRegex(RuntimeError, "не совпало"):
            native.read_state(self.context)

    def test_scoped_mutations_rejected_foreign_items(self):
        api = native.FixtureApi("http://127.0.0.1:8096/jellyfin")
        api.owned_ids.add(MOVIE)
        self.assertTrue(api.allows_request(f"/Items/{MOVIE}/Refresh", "POST"))
        self.assertFalse(api.allows_request(f"/Items/{EXISTING['Id']}/Refresh", "POST"))
        self.assertFalse(api.allows_request(f"/Items/{MOVIE}", "DELETE"))
        self.assertFalse(api.allows_request("/Library/VirtualFolders?name=Фильмы", "DELETE"))

    def test_failed_operation_closed_session_and_removed_header(self):
        with self.assertRaises(RuntimeError):
            with native.authenticated(self.api, "test", "private-password"):
                raise RuntimeError("fixture failed")
        self.assertEqual(self.api.calls[-1], ("POST", "/Sessions/Logout"))
        self.assertEqual(self.api.authorization, self.api.identity)

    def test_missing_range_bytes_did_not_pass(self):
        movie_path = self.cache / f"kinopoisk-{native.KP_ID}.mp4"
        movie_path.write_bytes(b"fixture" * 10000)
        state = {"movie_id": MOVIE, "library_id": LIBRARY_ID, "cache_dir": str(self.cache),
                 "cache_sha256": native.deploy.digest(movie_path), "baseline": native.digest_items([EXISTING])}
        self.api.damage_range = True
        with self.assertRaisesRegex(RuntimeError, "HTTP Range не совпал"):
            native.verify(self.api, self.context, state, USER)

    def test_single_version_count_omitted_by_jellyfin_was_handled(self):
        report = inventory.item_report(self.api, EXISTING)
        self.assertEqual(report["media_sources"], 1)
        explicit_null = dict(EXISTING, MediaSourceCount=None)
        self.assertEqual(inventory.item_report(self.api, explicit_null)["media_sources"], 1)
        series = copy.deepcopy(EXISTING)
        series["Type"] = "Series"
        self.assertIsNone(inventory.item_report(self.api, series)["media_sources"])

    def test_actual_range_transport_used_headers_and_base_url(self):
        captured = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                captured.append((self.path, dict(self.headers)))
                self.send_response(206)
                self.send_header("Content-Range", "bytes 0-3/100")
                self.send_header("Content-Length", "4")
                self.end_headers()
                self.wfile.write(b"test")

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            api = native.FixtureApi(f"http://127.0.0.1:{server.server_port}/jellyfin")
            api.owned_ids.add(TRAILER)
            api.authorization += ', Token="test-token"'
            body, content_range = api.read_range(TRAILER, 0, 3)
            self.assertEqual(body, b"test")
            self.assertEqual(content_range, "bytes 0-3/100")
            self.assertTrue(captured[0][0].startswith(f"/jellyfin/Videos/{TRAILER}/stream.mp4?"))
            self.assertNotIn("test-token", captured[0][0])
            self.assertEqual(captured[0][1]["Range"], "bytes=0-3")
            self.assertIn('Token="test-token"', captured[0][1]["Authorization"])
        finally:
            server.shutdown()
            thread.join(timeout=3)
            server.server_close()


if __name__ == "__main__":
    unittest.main()
