# Getting started

[← README](../README.md)

## Requirements

- **.NET SDK 10.0** (all projects target `net10.0`, see [`Directory.Build.props`](../Directory.Build.props)).
- **Windows** with a **Resonite install**. Every tool finds it the same way:
  1. `--resonite <dir>`
  2. `%RESONITE_DIR%`
  3. `C:\Program Files (x86)\Steam\steamapps\common\Resonite`

  Building the solution needs it as well: `Resonator.ShadersReferenceBuilder` links `SkyFrost.Base.dll` and
  `Elements.Core.dll` from the install. Set `RESONITE_DIR` or pass `-p:ResoniteDir=<dir>`.

## Build the tools

```powershell
$env:RESONITE_DIR = "D:\Games\Resonite"   # if not in the default Steam path
dotnet build Resonator.slnx -c Release
```

The executables land in `src/<Project>/bin/Release/net10.0/`.

## First run

```powershell
$pb = "src\Resonator.ProjectBuilder\bin\Release\net10.0\Resonator.ProjectBuilder.exe"

# one target -> the project goes straight into out\engine
& $pb out\engine --target FrooxEngine

# the whole Flatweave set -> out\flatweave\<target>\ per target, report every failure at the end
& $pb out\flatweave --set Flatweave --keep-going
```

The log shows each stage per target (`DEWEAVE` / `FLATTEN`, `DECOMPILE`, fixup counts, `BUILD OK`) and ends with `PIPELINE OK` or the
list of failed targets.

## What you get

For each target, in its project dir:

| Path | What |
|---|---|
| `**/*.cs` | the source tree: decompiled (or checked out), fixed and patched |
| `references.targets` | the generated reference set (HintPaths into the install, `PackageReference`s, properties, defines) |
| `<Name>.csproj` | from the template, imports `references.targets` (left out with `--no-csproj`) |
| `bin/Release/<tfm>/<Name>.dll` | the rebuilt assembly |

A `package` target (an official NuGet package) produces nothing: its dependents get a `PackageReference`.

Scratch data (prepared dlls, fetched sources, patch baselines) goes to `%TEMP%\resonator`.

## Integration mode (`--no-csproj`)

`--no-csproj` emits only the source tree and `references.targets`. The consumer compiles the tree from its own
csproj, so TFM and build settings stay its own:

```xml
<ItemGroup>
  <Compile Include="generated/**/*.cs" />
</ItemGroup>
<Import Project="generated/references.targets" />
```

This is how ArmAgedon consumes the [`ArmAginator`](../targets/ArmAginator/README.md) set.

## Next

- [Pipeline](pipeline.md): what each stage does.
- [Patches](patches.md): change the generated source and keep the change.
- [CLI reference](cli.md): every option.
