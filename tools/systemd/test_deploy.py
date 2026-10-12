"""Проверены состав пакета, восстановление данных и ошибки установки без живой службы."""

import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import deploy
import probe


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name in ("data", "config", "web", "backups"):
            (self.root / name).mkdir()
        (self.root / "data/plugins").mkdir()
        (self.root / "data/library.db").write_text("original database")
        (self.root / "config/system.xml").write_text("original config")
        (self.root / "web/index.html").write_text("original web")
        self.context = {"data": str(self.root / "data"), "config": str(self.root / "config"),
                        "web": str(self.root / "web"), "url": "http://127.0.0.1:8096",
                        "uid": os.getuid(), "gid": os.getgid(),
                        "index_uid": os.getuid(), "index_gid": os.getgid(),
                        "index_sha256": deploy.digest(self.root / "web/index.html"),
                        "bootstrap_enabled": True, "api_token_configured": False}

    def bundle(self, names=None):
        archive = self.root / "plugin.zip"
        with zipfile.ZipFile(archive, "w") as value:
            for name in names or sorted(deploy.DLLS):
                value.writestr(name, b"test dll")
        manifest = {"version": deploy.VERSION, "guid": deploy.GUID, "commit": deploy.COMMIT,
                    "archive_sha256": deploy.digest(archive),
                    "dll_sha256": {name: hashlib.sha256(b"test dll").hexdigest() for name in deploy.DLLS}}
        (self.root / "manifest.json").write_text(json.dumps(manifest))
        return manifest

    def snapshot(self):
        backup, state = deploy.create_backup(self.context, self.root / "backups")
        deploy.snapshot(backup, state)
        return backup

    def test_bundle_hashes_and_path_traversal(self):
        self.bundle()
        _, payload = deploy.load_bundle(self.root)
        self.assertEqual(set(payload), deploy.DLLS)
        self.bundle(["../Jellyfin.Plugin.Kinopoisk.dll", "KinopoiskUnofficialInfo.ApiClient.dll"])
        with self.assertRaises(RuntimeError):
            deploy.load_bundle(self.root)

    def test_corrupted_archive_rejected(self):
        self.bundle()
        with (self.root / "plugin.zip").open("ab") as value:
            value.write(b"corruption")
        with self.assertRaises(RuntimeError):
            deploy.load_bundle(self.root)

    def test_nested_configuration_saved_once(self):
        data = self.root / "data"
        self.assertEqual(deploy.roots_for(data, data / "config"), [data])

    def test_old_package_cleanup_preserved_cache(self):
        folder = self.root / "data/plugins/Jellyfin.Plugin.Kinopoisk"
        folder.mkdir()
        for name in deploy.DLLS:
            (folder / name).write_text("old")
        (folder / "cache").mkdir()
        (folder / "cache/item.json").write_text("cached")
        files, _ = deploy.package_files(self.context)
        self.assertEqual({p.name for p in files}, deploy.DLLS)
        for path in files:
            path.unlink()
        self.assertEqual((folder / "cache/item.json").read_text(), "cached")

    def old_package(self, folder_name="КиноПоиск_12.1.0.2"):
        folder = self.root / "data/plugins" / folder_name
        folder.mkdir()
        for name in deploy.DLLS:
            (folder / name).write_bytes(b"old dll")
        (folder / "meta.json").write_text(json.dumps({"guid": deploy.GUID, "name": "КиноПоиск",
                                                     "version": "12.1.0.2", "status": "Active"}))
        return folder

    @patch.object(deploy, "service")
    def test_install_retired_complete_packages_and_preserved_constant_cache(self, service):
        old = self.old_package()
        (old / "extra.txt").write_text("previous package data")
        canonical = self.old_package("Jellyfin.Plugin.Kinopoisk")
        (canonical / "cache").mkdir()
        (canonical / "cache/item.json").write_text("persistent cache")
        configurations = self.root / "data/plugins/configurations"
        configurations.mkdir()
        xml = configurations / "Jellyfin.Plugin.Kinopoisk.xml"
        xml.write_text("original plugin settings")
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)

        def check(context, candidate):
            self.assertFalse(old.exists())
            self.assertEqual((canonical / "cache/item.json").read_text(), "persistent cache")
            self.assertFalse((canonical / "meta.json").exists())
            self.assertFalse((canonical / "Jellyfin.Plugin.Kinopoisk.dll").exists())
            self.assertEqual(xml.read_text(), "original plugin settings")
            target = self.root / ("data/plugins/КиноПоиск_" + deploy.VERSION)
            deploy.verify_dlls(target, candidate)
            return {"startup": "PASS"}

        with patch.object(deploy, "check", side_effect=check):
            result = deploy.install(self.context, manifest, payload, self.root / "backups")
        saved = Path(result["backup"]) / "retired-plugin-folders" / old.name
        self.assertEqual((saved / "extra.txt").read_text(), "previous package data")
        self.assertEqual((saved / "Jellyfin.Plugin.Kinopoisk.dll").read_bytes(), b"old dll")
        self.assertEqual(json.loads((saved / "meta.json").read_text())["guid"], deploy.GUID)

    @patch.object(deploy, "service")
    def test_empty_old_and_newer_version_directories_retired_before_start(self, service):
        folders = [self.root / "data/plugins" / name for name in ("КиноПоиск_12.1.0.2", "КиноПоиск_99.0.0.0")]
        for folder in folders:
            folder.mkdir()
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        def start(action):
            if action == "start":
                self.assertTrue(all(not folder.exists() for folder in folders))
        service.side_effect = start
        with patch.object(deploy, "check", return_value={"startup": "PASS"}):
            deploy.install(self.context, manifest, payload, self.root / "backups")

    def test_unconfirmed_nonempty_version_folder_rejected_before_backup(self):
        folder = self.root / "data/plugins/КиноПоиск_12.1.0.2"
        folder.mkdir()
        (folder / "unknown.txt").write_text("foreign data")
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        with patch.object(deploy, "service") as service:
            with self.assertRaises(RuntimeError):
                deploy.install(self.context, manifest, payload, self.root / "backups")
            service.assert_not_called()
        self.assertEqual(list((self.root / "backups").iterdir()), [])
        self.assertTrue((folder / "unknown.txt").exists())

    def test_foreign_manifest_in_remaining_folder_rejected(self):
        folder = self.root / "data/plugins/КиноПоиск_12.1.0.2"
        folder.mkdir()
        (folder / "meta.json").write_text(json.dumps({"guid": "other-plugin"}))
        with self.assertRaises(RuntimeError):
            deploy.retired_package_folders(self.context, [])

    def test_symlink_version_folder_rejected(self):
        (self.root / "data/plugins/КиноПоиск_12.1.0.2").symlink_to(self.root / "web")
        with self.assertRaises(RuntimeError):
            deploy.retired_package_folders(self.context, [])

    def test_dll_mismatch_reported_missing_and_changed_files(self):
        manifest = self.bundle()
        _, target = deploy.package_files(self.context)
        target.mkdir()
        (target / "Jellyfin.Plugin.Kinopoisk.dll").write_bytes(b"unexpected")
        with self.assertRaises(RuntimeError) as failure:
            deploy.verify_dlls(target, manifest)
        detail = json.loads(str(failure.exception).split("; ", 1)[1])
        files = {item["file"]: item for item in detail["files"]}
        self.assertIsNone(files["KinopoiskUnofficialInfo.ApiClient.dll"]["actual_sha256"])
        self.assertEqual(files["Jellyfin.Plugin.Kinopoisk.dll"]["actual_sha256"], hashlib.sha256(b"unexpected").hexdigest())

    @patch.object(deploy, "wait_ready")
    @patch.object(deploy, "service")
    def test_failed_install_restored_retired_folders_and_saved_diagnostics(self, service, ready):
        old = self.old_package()
        empty = self.root / "data/plugins/КиноПоиск_10.11.0.10"
        empty.mkdir()
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        with patch.object(deploy, "check", side_effect=RuntimeError("post-start mismatch")):
            with self.assertRaises(RuntimeError):
                deploy.install(self.context, manifest, payload, self.root / "backups")
        self.assertEqual((old / "Jellyfin.Plugin.Kinopoisk.dll").read_bytes(), b"old dll")
        self.assertTrue(empty.is_dir())
        backup = next((self.root / "backups").iterdir())
        state = json.loads((backup / "state.json").read_text())
        self.assertEqual(state["phase"], "rolled_back")
        self.assertEqual(state["failure_type"], "RuntimeError")
        diagnostic = deploy.diagnose(self.context, manifest, backup)
        self.assertEqual(diagnostic["backup_phase"], "rolled_back")
        self.assertEqual(diagnostic["failed_installation"][0]["packages"][0]["meta"]["version"], deploy.VERSION)

    @patch.object(deploy, "wait_ready")
    @patch.object(deploy, "service")
    def test_diagnostic_failure_did_not_prevent_rollback(self, service, ready):
        old = self.old_package()
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        with patch.object(deploy, "check", side_effect=RuntimeError("startup failed")), \
                patch.object(deploy, "inspect_packages", side_effect=OSError("diagnostic denied")):
            with self.assertRaisesRegex(RuntimeError, "startup failed"):
                deploy.install(self.context, manifest, payload, self.root / "backups")
        self.assertEqual((old / "Jellyfin.Plugin.Kinopoisk.dll").read_bytes(), b"old dll")
        state = json.loads(next((self.root / "backups").glob("*/state.json")).read_text())
        self.assertEqual(state["phase"], "rolled_back")

    def test_diagnose_preserved_missing_binary_and_invalid_manifest_evidence(self):
        folder = self.root / "data/plugins/КиноПоиск_12.1.0.3"
        folder.mkdir()
        (folder / "meta.json").write_text("invalid json")
        manifest = self.bundle()
        result = deploy.diagnose(self.context, manifest)
        self.assertEqual(result["current_packages"][0]["meta"]["error"], "INVALID_MANIFEST")
        self.assertTrue(all(value is None for value in result["current_packages"][0]["dll_sha256"].values()))

    def test_diagnose_wrong_server_backup_rejected(self):
        backup = self.root / "backups/wrong-server"
        backup.mkdir()
        (backup / "state.json").write_text(json.dumps({"format": 1, "context": {"data": "other-server"}}))
        with self.assertRaises(RuntimeError):
            deploy.diagnose(self.context, self.bundle(), backup)

    @patch.object(deploy, "discover")
    @patch.object(deploy, "load_bundle")
    def test_diagnose_cli_did_not_require_valid_installation(self, bundle, discover):
        folder = self.root / ("data/plugins/КиноПоиск_" + deploy.VERSION)
        folder.mkdir()
        (folder / "unknown.txt").write_text("incomplete installation")
        bundle.return_value = self.bundle(), {}
        discover.return_value = self.context
        with patch.object(deploy.sys, "argv", ["deploy.py", "diagnose"]), \
                patch.object(deploy, "package_files", side_effect=AssertionError("not a preflight")), \
                patch.object(deploy.os, "umask"):
            self.assertEqual(deploy.main(), 0)

    @patch.object(deploy, "service")
    def test_repeated_same_version_install_preserved_previous_complete_package(self, service):
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        with patch.object(deploy, "check", return_value={"startup": "PASS"}):
            first = deploy.install(self.context, manifest, payload, self.root / "backups")
            second = deploy.install(self.context, manifest, payload, self.root / "backups")
        self.assertNotEqual(first["backup"], second["backup"])
        saved = Path(second["backup"]) / "retired-plugin-folders" / ("КиноПоиск_" + deploy.VERSION)
        deploy.verify_dlls(saved, manifest)
        self.assertEqual(json.loads((saved / "meta.json").read_text())["version"], deploy.VERSION)

    @patch.object(deploy, "service")
    def test_prestart_dll_corruption_restored_previous_package(self, service):
        old = self.old_package()
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        payload["Jellyfin.Plugin.Kinopoisk.dll"] = b"corrupted before start"
        with patch.object(deploy, "wait_ready"), patch.object(deploy, "check") as check:
            with self.assertRaisesRegex(RuntimeError, "Установленные DLL"):
                deploy.install(self.context, manifest, payload, self.root / "backups")
            check.assert_not_called()
        self.assertEqual((old / "Jellyfin.Plugin.Kinopoisk.dll").read_bytes(), b"old dll")

    def test_foreign_package_rejected(self):
        folder = self.root / "data/plugins/shared"
        folder.mkdir()
        (folder / "Jellyfin.Plugin.Kinopoisk.dll").write_text("old")
        (folder / "AnotherPlugin.dll").write_text("foreign")
        with self.assertRaises(RuntimeError):
            deploy.package_files(self.context)

    @patch.object(deploy, "wait_ready")
    @patch.object(deploy, "service")
    def test_full_rollback_preserved_test_state(self, service, ready):
        backup = self.snapshot()
        (self.root / "data/library.db").write_text("test database")
        (self.root / "data/new-file").write_text("new")
        (self.root / "config/system.xml").write_text("test config")
        result = deploy.rollback(backup)
        self.assertEqual(result["rollback"], "PASS")
        self.assertEqual((self.root / "data/library.db").read_text(), "original database")
        self.assertFalse((self.root / "data/new-file").exists())
        self.assertEqual((self.root / "config/system.xml").read_text(), "original config")
        saved_data = next(Path(p) for p in result["saved_test_state"] if Path(p).name.startswith("data."))
        self.assertEqual((saved_data / "library.db").read_text(), "test database")

    @patch.object(deploy, "wait_ready")
    @patch.object(deploy, "service")
    def test_extract_failure_returned_current_directories(self, service, ready):
        backup = self.snapshot()
        (self.root / "data/library.db").write_text("current database")
        with patch.object(deploy, "run", side_effect=RuntimeError("extract failed")):
            with self.assertRaises(RuntimeError):
                deploy.rollback(backup)
        self.assertEqual((self.root / "data/library.db").read_text(), "current database")
        self.assertEqual((self.root / "config/system.xml").read_text(), "original config")
        state = json.loads((backup / "state.json").read_text())
        self.assertEqual(state["phase"], "rollback_failed")

    @patch.object(deploy, "wait_ready")
    @patch.object(deploy, "service")
    def test_failed_check_restored_original_database(self, service, ready):
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        with patch.object(deploy, "check", side_effect=RuntimeError("startup failed")):
            with self.assertRaises(RuntimeError):
                deploy.install(self.context, manifest, payload, self.root / "backups")
        self.assertEqual((self.root / "data/library.db").read_text(), "original database")
        self.assertFalse((self.root / ("data/plugins/КиноПоиск_" + deploy.VERSION)).exists())
        state = json.loads(next((self.root / "backups").glob("*/state.json")).read_text())
        self.assertEqual(state["phase"], "rolled_back")

    @patch.object(deploy, "wait_ready")
    @patch.object(deploy, "service")
    def test_snapshot_failure_restarted_without_installation(self, service, ready):
        manifest = self.bundle()
        _, payload = deploy.load_bundle(self.root)
        with patch.object(deploy, "snapshot", side_effect=RuntimeError("backup failed")):
            with self.assertRaises(RuntimeError):
                deploy.install(self.context, manifest, payload, self.root / "backups")
        self.assertFalse((self.root / ("data/plugins/КиноПоиск_" + deploy.VERSION)).exists())
        self.assertIn(unittest.mock.call("start"), service.call_args_list)

    @patch.object(deploy, "request")
    def test_probe_closed_session_after_route_failure(self, request):
        authentication = {"AccessToken": "test-token", "User": {"Id": "test", "Policy": {"IsAdministrator": True}}}
        request.side_effect = [(200, {}, json.dumps(authentication).encode()), (500, {}, b"{}"), (204, {}, b"")]
        with self.assertRaises(RuntimeError):
            probe.session_probe("http://127.0.0.1:8096", "test", "test")
        self.assertTrue(request.call_args_list[-1].args[0].endswith("/Sessions/Logout"))
        self.assertIn('Token="test-token"', request.call_args_list[1].args[1]["Authorization"])
        self.assertNotIn("X-Emby-Token", request.call_args_list[1].args[1])

    @patch.object(deploy, "request")
    def test_probe_regular_user_admin_routes_forbidden(self, request):
        authentication = {"AccessToken": "test-token", "User": {"Id": "test", "Policy": {"IsAdministrator": False}}}
        request.side_effect = [(200, {}, json.dumps(authentication).encode()), (200, {}, b"{}"),
                               (403, {}, b""), (403, {}, b""), (200, {}, b'{"TotalRecordCount": 0}'), (204, {}, b"")]
        result = probe.session_probe("http://127.0.0.1:8096", "test", "test")
        self.assertEqual(result["admin_routes"], "CORRECTLY_FORBIDDEN")
        self.assertEqual(result["visible_movies_and_series"], 0)

    @patch.object(deploy, "request")
    def test_probe_admin_plugin_and_cache_verified(self, request):
        authentication = {"AccessToken": "test-token", "User": {"Id": "test", "Policy": {"IsAdministrator": True}}}
        plugin = [{"Id": deploy.GUID, "Version": deploy.VERSION, "Status": "Active"}]
        request.side_effect = [(200, {}, json.dumps(authentication).encode()), (200, {}, b"{}"),
                               (200, {}, json.dumps(plugin).encode()), (200, {}, b"{}"),
                               (200, {}, b'{"TotalRecordCount": 2}'), (204, {}, b"")]
        result = probe.session_probe("http://127.0.0.1:8096", "test", "test")
        self.assertEqual(result["admin_routes"], "PASS")
        self.assertEqual(result["visible_movies_and_series"], 2)

    @patch.object(deploy, "request")
    @patch.object(deploy, "wait_ready", return_value={"Version": "12.1.0"})
    def test_startup_check_runtime_injection_and_bundle(self, ready, request):
        manifest = self.bundle()
        _, target = deploy.package_files(self.context)
        target.mkdir()
        for name in deploy.DLLS:
            (target / name).write_bytes(b"test dll")
        (target / "meta.json").write_text(json.dumps({"guid": deploy.GUID, "status": "Active", "version": deploy.VERSION}))
        body = b"test webclient"
        manifest["webclient_sha256"] = hashlib.sha256(body).hexdigest()
        html = b'<script src="../Kinopoisk/WebClient.js?v=123" data-kinopoisk-managed="runtime"></script>'
        request.side_effect = [(200, {"Content-Type": "application/javascript"}, body),
                               (200, {"X-Kinopoisk-Web-Bootstrap": "runtime"}, html), (401, {}, b"")]
        result = deploy.check(self.context, manifest)
        self.assertEqual(result["startup"], "PASS")
        self.assertEqual(result["functional_acceptance"], "PENDING")
        request.side_effect = [(200, {"Content-Type": "application/javascript"}, body),
                               (200, {"X-Kinopoisk-Web-Bootstrap": "runtime"}, html + html)]
        with self.assertRaises(RuntimeError):
            deploy.check(self.context, manifest)

    def test_backup_inside_data_rejected(self):
        with self.assertRaises(RuntimeError):
            deploy.create_backup(self.context, self.root / "data/backups")

    @patch.object(deploy.time, "sleep")
    @patch.object(deploy.time, "monotonic", side_effect=[0, 0, 121])
    @patch.object(deploy, "server_info", side_effect=RuntimeError("not ready"))
    @patch.object(deploy, "request", return_value=(503, {}, b"Unhealthy"))
    @patch.object(deploy, "service", return_value="active")
    def test_startup_timeout_reported_health_and_service(self, service, request, info, clock, sleep):
        with self.assertRaises(RuntimeError) as result:
            deploy.wait_ready("http://127.0.0.1:8096/jellyfin")
        self.assertIn('"health_http": 503', str(result.exception))
        self.assertIn('"health": "Unhealthy"', str(result.exception))
        self.assertIn('"service": "active"', str(result.exception))

    @patch.object(deploy.time, "monotonic", side_effect=[0, 0])
    @patch.object(deploy, "server_info", return_value={"Version": "12.1.0"})
    @patch.object(deploy, "request", return_value=(200, {}, b"Healthy"))
    @patch.object(deploy, "service", return_value="active")
    def test_ready_server_accepted_with_base_url(self, service, request, info, clock):
        self.assertEqual(deploy.wait_ready("http://127.0.0.1:8096/jellyfin")["Version"], "12.1.0")
        request.assert_called_once_with("http://127.0.0.1:8096/jellyfin/health")


if __name__ == "__main__":
    unittest.main()
