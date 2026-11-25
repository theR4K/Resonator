"""Publish the managed tool suite and its own sources/configuration, excluding game binaries."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TOOL_FRAMEWORK = ET.parse(ROOT / "Directory.Build.props").findtext("./PropertyGroup/TargetFramework")
TOOLS = ("Resonator.ProjectBuilder", "Resonator.PatchGenerator", "Resonator.Deweaver", "Resonator.ShadersReferenceBuilder")
GAME_BINARIES = {"frooxengine.dll", "frooxengine.weaver.dll", "elements.core.dll", "elements.assets.dll",
                 "awwdio.dll", "photondust.dll", "skyfrost.base.dll", "skyfrost.base.models.dll", "renderite.shared.dll"}


def validate_version(version):
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("Use a release version such as 0.1.0 or 0.1.0-rc.1")
    return version


def check_bundle(bundle):
    forbidden = [p for p in bundle.rglob("*") if p.is_file() and (p.name.lower() in GAME_BINARIES or p.suffix.lower() in {".exe", ".nupkg"})]
    if forbidden:
        raise ValueError("Unexpected game/native binary in tool release: " + ", ".join(str(p) for p in forbidden))
    for tool in TOOLS:
        if not (bundle / "tools" / tool / (tool + ".dll")).is_file():
            raise ValueError("Missing published tool: " + tool)
        runtime = bundle / "tools" / tool / (tool + ".runtimeconfig.json")
        if not runtime.is_file() or json.loads(runtime.read_text(encoding="utf-8"))["runtimeOptions"]["tfm"] != TOOL_FRAMEWORK:
            raise ValueError("Unexpected tool runtime framework: " + tool)
    for path in ("Resonator.slnx", "targets/ArmAginator/set.json", "src/Flatweave.Adapter/Flatweave.Adapter.csproj"):
        if not (bundle / path).is_file():
            raise ValueError("Missing target lookup/runtime source: " + path)


def package(version, output):
    version = validate_version(version)
    output = output.resolve()
    # Refuse to overwrite an existing staging tree; release retries should use a fresh output directory.
    bundle = output / f"Resonator-{version}"
    bundle.mkdir(parents=True, exist_ok=False)
    for tool in TOOLS:
        subprocess.run(["dotnet", "publish", str(ROOT / "src" / tool), "-c", "Release", "--no-restore",
                        "--self-contained", "false", "-p:UseAppHost=false", f"-p:Version={version}",
                        "-o", str(bundle / "tools" / tool)], cwd=ROOT, check=True)
    for directory in ("targets", "docs", "src/Flatweave.Adapter"):
        shutil.copytree(ROOT / directory, bundle / directory,
                        ignore=shutil.ignore_patterns("bin", "obj", ".git", "__pycache__"))
    for name in ("README.md", "LICENSE", "Directory.Build.props", "Resonator.slnx"):
        shutil.copy2(ROOT / name, bundle / name)
    (bundle / "RELEASE.md").write_text(
        "Requires the .NET 11 SDK and your own Resonite install.\n\n"
        "Run `dotnet tools/Resonator.ProjectBuilder/Resonator.ProjectBuilder.dll --help`.\n"
        "Targets are bundled and located automatically. Set RESONITE_DIR or pass --resonite.\n"
        "This portable package runs with native .NET on x64 and ARM64; it contains no Resonite binaries.\n",
        encoding="utf-8")
    check_bundle(bundle)
    manifest = {str(p.relative_to(bundle)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted(bundle.rglob("*")) if p.is_file()}
    (bundle / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    archive = output / (bundle.name + ".zip")
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as stream:
        for file in sorted(bundle.rglob("*")):
            if file.is_file():
                stream.write(file, file.relative_to(output))
    (output / "SHA256SUMS").write_text(f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n", encoding="utf-8")
    print(f"Release package ready: {archive}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "out/release")
    options = parser.parse_args()
    package(options.version, options.output)
