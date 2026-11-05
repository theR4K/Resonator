"""Run the full Flatweave pipeline, treating failed patch hunks as CI failures."""
import argparse
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def run(resonite, output, target):
    output.mkdir(parents=True, exist_ok=True)
    log = output / "pipeline.log"
    command = ["dotnet", str(ROOT / "src/Resonator.ProjectBuilder/bin/Release/net10.0/Resonator.ProjectBuilder.dll"),
               str(output / "projects"), "--targets", str(ROOT / "targets/Flatweave"),
               "--resonite", str(resonite.resolve()), "--keep-going", "--no-publish"]
    if target:
        command += ["--target", target]
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace")
        for line in process.stdout:
            sys.stdout.write(line)
            stream.write(line)
        code = process.wait()
    text = log.read_text(encoding="utf-8")
    if code or "patch ERROR" in text or "hunk(s) did not match" in text or "PIPELINE OK" not in text:
        print(f"Resonite pipeline validation failed; see {log}", file=sys.stderr)
        return 1
    print("Full pipeline and patch application checks passed.")
    return 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resonite", type=Path, default=Path(os.environ.get("RESONITE_DIR", ".")))
    parser.add_argument("--output", type=Path, default=ROOT / "out/validation")
    parser.add_argument("--target", default="")
    options = parser.parse_args()
    sys.exit(run(options.resonite, options.output.resolve(), options.target))
