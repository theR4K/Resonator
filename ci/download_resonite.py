"""Fetch build inputs from Steam, using a native ARM64 downloader or SteamCMD."""
import argparse
import hashlib
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

DEPOT_TAG = "DepotDownloader_3.4.0"


def downloader_asset(system=None, machine=None):
    system = system or platform.system()
    machine = (machine or platform.machine()).lower()
    architecture = {"aarch64": "arm64", "arm64": "arm64", "amd64": "x64", "x86_64": "x64"}.get(machine)
    operating_system = {"Linux": "linux", "Windows": "windows", "Darwin": "macos"}.get(system)
    if not architecture or not operating_system:
        raise ValueError(f"Unsupported downloader host: {system}/{machine}")
    return f"DepotDownloader-{operating_system}-{architecture}.zip"


def depot_command(home):
    asset = downloader_asset()
    install = home / "tools" / DEPOT_TAG / asset.removesuffix(".zip")
    executable = install / ("DepotDownloader.exe" if os.name == "nt" else "DepotDownloader")
    if not executable.exists():
        install.mkdir(parents=True, exist_ok=True)
        archive = install / asset
        url = f"https://github.com/SteamRE/DepotDownloader/releases/download/{DEPOT_TAG}/{asset}"
        print(f"Downloading pinned Steam client: {DEPOT_TAG}/{asset}", flush=True)
        urllib.request.urlretrieve(url, archive)
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(install)
        archive.unlink()
        executable.chmod(executable.stat().st_mode | 0o111)
    return str(executable)


def download(destination, method, home):
    destination, home = destination.resolve(), home.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    home.mkdir(parents=True, exist_ok=True)
    if os.name != "nt":
        home.chmod(0o700)
    username = os.environ.get("STEAM_USERNAME", "")
    password = os.environ.get("STEAM_PASSWORD", "")
    if not username:
        raise ValueError("Set the STEAM_USERNAME secret to a Steam account with Resonite in its library.")
    branch = os.environ.get("RESONITE_BRANCH", "public")
    if method == "depotdownloader":
        filelist = home / "resonite-build-inputs.txt"
        filelist.write_text(r"regex:(?i).*\.(dll|pdb|config)$" + "\n", encoding="utf-8")
        command = [depot_command(home), "-app", "2519830", "-os", "windows", "-osarch", "64",
                   "-dir", str(destination), "-filelist", str(filelist), "-validate",
                   "-username", username, "-remember-password", "-branch", branch]
        if password:
            command += ["-password", password]
    else:
        executable = os.environ.get("STEAMCMD", "") or shutil.which("steamcmd") or shutil.which("steamcmd.sh")
        if not executable:
            raise ValueError("Set STEAMCMD to a working SteamCMD launcher. On Linux ARM64 it needs x86 emulation; use depotdownloader for native ARM64.")
        command = [executable, "+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1",
                   "+@sSteamCmdForcePlatformType", "windows", "+force_install_dir", str(destination),
                   "+login", username]
        if password:
            command.append(password)
        command += ["+app_update", "2519830"]
        if branch != "public":
            command += ["-beta", branch]
        command += ["validate", "+quit"]
    # A remembered Steam session belongs in the private Steam home, never in a release or artifact.
    # A first-time Steam Guard prompt cannot be answered in CI: bootstrap the session on the runner.
    subprocess.run(command, cwd=home, check=True, stdin=subprocess.DEVNULL, timeout=1800)
    required = ["FrooxEngine.dll", "FrooxEngine.Weaver.dll", "SkyFrost.Base.dll", "Elements.Core.dll"]
    missing = [name for name in required if not (destination / name).is_file()]
    if missing:
        raise ValueError("Steam download did not produce the expected install: " + ", ".join(missing))
    digest = hashlib.sha256((destination / "FrooxEngine.dll").read_bytes()).hexdigest()
    print(f"Resonite build inputs ready; FrooxEngine SHA256={digest}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--method", choices=["depotdownloader", "steamcmd"], default="depotdownloader")
    parser.add_argument("--steam-home", type=Path, default=Path(os.environ.get("RESONATOR_STEAM_HOME") or str(Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir())) / "resonator-steam")))
    options = parser.parse_args()
    try:
        download(options.destination, options.method, options.steam_home)
    except subprocess.CalledProcessError as error:
        # CalledProcessError.__str__ includes the command, which may contain the Steam password.
        print(f"Steam client failed with exit code {error.returncode}. Check account/session authorization.", file=sys.stderr)
        sys.exit(1)
    except subprocess.TimeoutExpired:
        print("Steam download timed out. Bootstrap the remembered Steam Guard session on the runner.", file=sys.stderr)
        sys.exit(1)
    except ValueError as error:
        print(f"Steam download failed: {error}", file=sys.stderr)
        sys.exit(1)
