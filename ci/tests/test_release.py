"""CI boundary checks: architecture, private inputs and publication behavior."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import urllib.error
from unittest import mock

CI = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, CI / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


download = module("download_resonite")
package = module("package_tools")
release = module("publish_release")


class ReleaseTests(unittest.TestCase):
    def test_linux_login_state_survives_a_new_process_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with mock.patch.object(download.platform, "system", return_value="Linux"):
                with mock.patch.dict(download.os.environ, {"XDG_DATA_HOME": "/temporary-container-home"}):
                    login_environment = download.depot_environment(root)
                with mock.patch.dict(download.os.environ, {"XDG_DATA_HOME": "/another-container-home"}):
                    job_environment = download.depot_environment(root)
            self.assertEqual(login_environment["XDG_DATA_HOME"], job_environment["XDG_DATA_HOME"])
            self.assertTrue(Path(job_environment["XDG_DATA_HOME"]).is_dir())

    def test_native_downloader_selection(self):
        self.assertEqual(download.downloader_asset("Linux", "aarch64"), "DepotDownloader-linux-arm64.zip")
        self.assertEqual(download.downloader_asset("Windows", "ARM64"), "DepotDownloader-windows-arm64.zip")
        self.assertEqual(download.downloader_asset("Windows", "AMD64"), "DepotDownloader-windows-x64.zip")
        with self.assertRaises(ValueError):
            download.downloader_asset("Linux", "mips")

    def test_release_version_cannot_escape_output(self):
        for bad in ("../stolen", "1.0", "v1.0.0", "1.0.0/extra", "1.0.0;echo bad"):
            with self.assertRaises(ValueError):
                package.validate_version(bad)
        self.assertEqual(package.validate_version("1.0.0-rc.1"), "1.0.0-rc.1")

    def test_game_dll_cannot_enter_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "FrooxEngine.dll").write_bytes(b"private game reference")
            with self.assertRaisesRegex(ValueError, "game/native"):
                package.check_bundle(root)

    def test_draft_is_published_only_after_uploads(self):
        class Client:
            def __init__(self):
                self.operations = []

            def request(self, method, path, data=None):
                self.operations.append((method, path, data))
                if path.startswith("/releases/tags/"):
                    raise urllib.error.HTTPError("https://forgejo", 404, "missing", {}, None)
                if method == "POST":
                    return {"id": 7, "draft": True}
                if method == "GET":
                    return []

            def upload(self, release_id, file):
                self.operations.append(("UPLOAD", file.name, None))

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            for name in ("Resonator-1.0.0.zip", "SHA256SUMS"):
                (output / name).write_bytes(b"test")
            client = Client()
            release.publish(client, "v1.0.0", output, "abc123")
            self.assertEqual(client.operations[-1], ("PATCH", "/releases/7", {"draft": False}))
            self.assertEqual([op[1] for op in client.operations if op[0] == "UPLOAD"], ["Resonator-1.0.0.zip", "SHA256SUMS"])

    def test_published_release_is_immutable(self):
        class Client:
            def request(self, *args):
                return {"id": 7, "draft": False}

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            for name in ("Resonator-1.0.0.zip", "SHA256SUMS"):
                (output / name).write_bytes(b"test")
            with self.assertRaisesRegex(ValueError, "already published"):
                release.publish(Client(), "v1.0.0", output, "abc123")


if __name__ == "__main__":
    unittest.main()
