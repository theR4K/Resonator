# Forgejo releases and Resonite validation

The workflows live in `.forgejo/workflows`:

- `release.yaml`: a `vMAJOR.MINOR.PATCH` tag builds/tests the suite, creates a portable archive and SHA256SUMS, uploads them to a Forgejo draft, then publishes it. Manual dispatch accepts an existing tag. Published releases are never overwritten; an interrupted draft can be retried.
- The daily check for new Resonite builds lives in the private `resonator-publish` repository: for each new Steam build (or new tool release) it builds the Resonator, Flatweave and ArmAginator sets with the latest tool release and publishes them as drop-in bundles. A failed patch hunk fails that run. `ci/validate_pipeline.py` still runs the Flatweave rebuild locally.
- `checks.yaml`: runs on branch pushes/pull requests and can be dispatched manually. It builds the three CLIs that have no game-reference dependency, runs source-only remapping/adapter tests and the release helper tests. This checks the native runner without Steam credentials. ShadersReferenceBuilder and original-Assimp differential checks run in the download-backed workflows.

## Runner setup

The default is a **Linux runner**, including ARM64. `checks.yaml` runs on `ubuntu-latest` (the home lab's runner `pi`; repository variable `RESONATOR_RUNNER` overrides it). `release.yaml` needs Steam and runs on the runner labelled `steam` (variable `RESONATOR_STEAM_RUNNER` overrides it), which mounts the persistent Steam directory at `/resonator-steam` in every job and runs one job at a time. It needs Python 3.10+, Git, a shell, and Node.js 24 for the pinned setup-dotnet action; setup-dotnet installs the native .NET 10 SDK. No Docker privileged mode or emulator is needed for the default download method. The CI scripts are portable Python; the supplied workflow shell commands target Linux.

The release archive is framework dependent and portable: the same ZIP runs with native .NET on Linux/Windows/macOS, ARM64 or x64. It contains all four CLIs, target sets/patches, docs, and the local Flatweave.Adapter source needed by those targets. Run:

```
dotnet tools/Resonator.ProjectBuilder/Resonator.ProjectBuilder.dll --help
```

The .NET 10 SDK and a user's own Resonite install are required. Targets are located automatically from the bundled repository marker. Resonite assemblies, generated engine trees and Steam login state are excluded from tool releases. Validation builds are not uploaded as tool-suite release assets.

## Steam authentication and ARM64

Set secret `STEAM_USERNAME` to an account with Resonite in its library. Set `STEAM_PASSWORD` only if needed for a noninteractive login; prefer a remembered session bootstrapped on the runner with Steam Guard. Set variable `RESONATOR_STEAM_HOME` to a private persistent directory visible to each job (and inside the job container if using Docker). Without it, the default is under the runner's temporary directory and the login will not persist reliably.

The default **DepotDownloader 3.4.0** is a Steam CLI using SteamKit2, with native Linux ARM64 and Windows ARM64 releases. The helper downloads the matching native binary, requests Windows x64 Resonite depots for their managed reference assemblies, and does not execute the game. The selected game architecture does not change the downloader's host architecture. Only DLL/PDB/config build inputs are fetched with this method.

For first-time authorization, use the interactive helper from the Resonator checkout:

```sh
python3 ci/steam_login.py --username YOUR_STEAM_LOGIN --steam-home /resonator-steam
```

Enter the password at the terminal prompt and approve Steam Guard. This fetches manifests only. Unlike the CI downloader, this helper permits interactive input. Both helpers put Linux .NET isolated storage under `/resonator-steam/user-data`: DepotDownloader does not save login state in its working directory. The executable's absolute path also contributes to the isolated-storage identity, so use the same `/resonator-steam` path for bootstrap and jobs.

For Docker runners, mount a private persistent directory or named volume at `/resonator-steam` in both the bootstrap container and the Steam-backed job containers. Set `RESONATOR_STEAM_HOME=/resonator-steam` as a Forgejo repository variable and `STEAM_USERNAME` as a repository secret. The job user must be able to read/write the mounted directory. Mounting this directory only in the runner daemon container does not mount it into job containers; configure the job container mounts in your runner setup. The supplied workflow does not create host mounts automatically.

Keep the directory private and never upload it or put it in a shared action cache. A password alone may still require Steam Guard; CI cannot complete an interactive prompt.

To use Valve **SteamCMD** instead, set `RESONATOR_DOWNLOADER=steamcmd` and `STEAMCMD` to an installed executable/launcher visible in the job. On Linux ARM64, provide a working x86 emulation launcher; changing the requested depot architecture does not make SteamCMD an ARM binary. The workflow deliberately does not install host emulation or request privileged containers. SteamCMD fetches the full app rather than the managed-only file selection. On Windows ARM64, test the installed SteamCMD under Windows' x86 compatibility support first.

Steam tools never ship with the Resonator archive. Downloader flags and session behavior are documented by [DepotDownloader](https://github.com/SteamRE/DepotDownloader); [Resonite's SteamCMD instructions](https://wiki.resonite.com/Headless_Server_Software/Setup) cover account authorization (the public-client workflow here does not request the restricted headless branch).

## Forgejo configuration

Enable Actions for the repository and make the runner available to it. Forgejo supplies the workflow repository token for release creation/upload, so a separate publishing PAT is unnecessary. The automatic token needs repository write access; this is the standard Forgejo token for trusted tag/manual workflows. Release publishing does not run on pull requests.

Optional repository variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `RESONATOR_RUNNER` | `ubuntu-latest` | Runner label for `checks.yaml` |
| `RESONATOR_STEAM_RUNNER` | `steam` | Runner label for `release.yaml` |
| `RESONATOR_STEAM_HOME` | `/resonator-steam` | Private persistent Steam session/tool directory |
| `RESONATOR_DOWNLOADER` | `depotdownloader` | `depotdownloader` or `steamcmd` |
| `STEAMCMD` | executable on PATH | Preinstalled SteamCMD launcher |

The build uses Resonite references for ShadersReferenceBuilder; `Private=false` prevents those game assemblies from being copied into the tool output. Packaging also rejects core game DLLs and apphost executables. Tool archives are versioned, include a per-file SHA256 manifest, and have an external SHA256SUMS file.

## Local verification

```
python3 -m unittest discover -s ci/tests -v
dotnet build Resonator.slnx -c Release -p:ResoniteDir=/path/to/Resonite
dotnet run --project tests/Resonator.Remapping.Tests -c Release --no-build
python3 ci/package_tools.py --version 0.1.0 --output out/release-check
python3 ci/validate_pipeline.py --resonite /path/to/Resonite --target Awwdio
```

Set `RESONITE_DIR` for the package command too: it publishes ShadersReferenceBuilder against that install. Use a fresh package output directory for each check. Full generated Flatweave validation can take considerably longer than the tool build.
