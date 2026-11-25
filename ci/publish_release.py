"""Upload only tool archives/checksums to a Forgejo draft, then publish it."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import uuid


class Forgejo:
    def __init__(self, base, repository, token):
        if not base.startswith("https://") or not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
            raise ValueError("Expected an HTTPS Forgejo URL and owner/repository")
        self.base = base.rstrip("/") + "/api/v1/repos/" + repository
        self.token = token

    def request(self, method, path, data=None, content_type="application/json"):
        if isinstance(data, dict):
            data = json.dumps(data).encode()
        request = urllib.request.Request(self.base + path, method=method, data=data,
            headers={"Authorization": "token " + self.token, "Content-Type": content_type, "Accept": "application/json"})
        with urllib.request.urlopen(request, timeout=120) as response:
            body = response.read()
            return json.loads(body) if body else None

    def upload(self, release_id, file):
        boundary = uuid.uuid4().hex
        data = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"attachment\"; filename=\"{file.name}\"\r\n"
                "Content-Type: application/octet-stream\r\n\r\n").encode() + file.read_bytes() + f"\r\n--{boundary}--\r\n".encode()
        return self.request("POST", f"/releases/{release_id}/assets?name={urllib.parse.quote(file.name)}", data,
                            "multipart/form-data; boundary=" + boundary)


def publish(client, tag, output, commit):
    if not re.fullmatch(r"v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", tag):
        raise ValueError("Release tag must be vMAJOR.MINOR.PATCH, optionally with a prerelease suffix")
    files = [output / f"Resonator-{tag[1:]}.zip", output / "SHA256SUMS"]
    if not all(file.is_file() for file in files):
        raise ValueError("Build the release archive and checksums first")
    try:
        release = client.request("GET", "/releases/tags/" + urllib.parse.quote(tag, safe=""))
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = client.request("POST", "/releases", {"tag_name": tag, "target_commitish": commit,
            "name": "Resonator " + tag, "draft": True, "prerelease": "-" in tag,
            "body": "Portable .NET tool suite: ProjectBuilder, PatchGenerator, Deweaver and ShadersReferenceBuilder.\n\n"
                    "Includes target sets, patches and shared adapter source. Requires the .NET 10 and 11 SDKs and your own Resonite install. Generated game projects and ShadersReferenceBuilder target .NET 11. "
                    "Runs with native .NET on x64 and ARM64. Resonite binaries and generated engine sources are excluded.\n\n"
                    "Verify the archive with SHA256SUMS. Source commit: " + commit})
    if not release["draft"]:
        raise ValueError("This release is already published; use a new version instead of replacing public assets")
    assets = client.request("GET", f"/releases/{release['id']}/assets?limit=100")
    for file in files:
        for asset in assets:
            if asset["name"] == file.name:
                client.request("DELETE", f"/releases/{release['id']}/assets/{asset['id']}")
        client.upload(release["id"], file)
    client.request("PATCH", f"/releases/{release['id']}", {"draft": False})
    print("Published Forgejo tool release " + tag)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    tagged_commit = subprocess.check_output(["git", "rev-parse", options.tag + "^{commit}"], text=True).strip()
    if commit != tagged_commit:
        raise SystemExit("Checked-out commit does not match the release tag")
    client = Forgejo(os.environ["FORGEJO_SERVER_URL"], os.environ["FORGEJO_REPOSITORY"], os.environ["FORGEJO_TOKEN"])
    publish(client, options.tag, options.output, commit)
