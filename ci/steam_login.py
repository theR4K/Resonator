"""Authorize Steam interactively once, using the same downloader/state path as CI."""
import argparse
from pathlib import Path
import subprocess
from download_resonite import depot_command, depot_environment


def login(username, home):
    home = home.resolve()
    home.mkdir(parents=True, exist_ok=True)
    environment = depot_environment(home)
    command = [depot_command(home), "-app", "2519830", "-os", "windows", "-osarch", "64",
               "-username", username, "-remember-password", "-manifest-only", "-dir", str(home / "manifests")]
    print("Enter your Steam password when asked, then approve Steam Guard. No game assets will be downloaded.", flush=True)
    subprocess.run(command, cwd=home, env=environment, check=True)
    print("Steam authorization completed. Reuse this directory at the SAME absolute path inside CI job containers.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--username", required=True)
    parser.add_argument("--steam-home", type=Path, required=True)
    args = parser.parse_args()
    login(args.username, args.steam_home)
