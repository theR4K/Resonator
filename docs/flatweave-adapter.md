# Flatweave.Adapter

`src/Flatweave.Adapter` is a standalone .NET 11 class library. It has no Resonite assemblies, generated engine
sources, native libraries or Resonator-tool dependencies. Its only package dependency is Concentus 2.2.2.

```powershell
dotnet build src/Flatweave.Adapter -c Release
```

The `Flatweave.Adapter` target exports the same source into an ordinary generated project:

```powershell
Resonator.ProjectBuilder out/adapter --set Flatweave --target Flatweave.Adapter
```

Local-only target selection does not require an installed game. The target uses
`"source": { "local": "../../../src/Flatweave.Adapter" }`, resolved relative to its target directory.
Local source is never fetched or cached: regeneration sees current edits. Build output is excluded, and input/output
directories cannot overlap. Generated projects receive their dependency/version settings from the target spec.

## Contents

| Implementation | Purpose |
|---|---|
| `Resonator.FlattenedInitOnlyAttribute` | one shared marker for sync fields whose readonly flag was cleared |
| `Flatweave.Adapters.FlattenedFields.IsInitOnlyOrFlattened` | shared reflection semantics for field discovery and compatibility hashes; also recognizes historical embedded markers |
| `Flatweave.Adapters.AssimpMath` | original AssimpNet matrix conventions, operations and decomposition over Numerics |
| `Flatweave.Adapters.OpusEncoder` / `OpusDecoder` | shipped float-codec contracts implemented with Concentus, including DTX, PLC and FEC handling |

The flattener imports the shared attribute constructor instead of embedding a definition into each assembly.
Its cache version is bumped, so old prepared DLLs regenerate. Every flattened target automatically references the
adapter source target; source dependencies build in dependency order, with cycles rejected.

Assimp and Opus type remapping directs generated engine code to the shared library. Engine patches contain only
behaviour/API seams, not helper implementations. Opus constructors/settings use integer values so the adapter does
not depend on POpusCodec. The engine's existing enum types remain unchanged in sync fields and serialized data.

## ArmAginator consumers

ArmAginator replaces the target with the same local sources and a reference to its pinned `Concentus` source target,
instead of a package reference. A source-only consumer generates `Flatweave.Adapter` and builds it alongside its own
Concentus project. ArmAgedon does this in `src/ThirdParty/Flatweave.Adapter`; FrooxEngine references that project.

```powershell
Resonator.ProjectBuilder <consumer>/generated --set ArmAginator --target Flatweave.Adapter --no-csproj
```

The adapter is managed and AnyCPU. It introduces no engine data-model types. Regression checks cover external marker
identity, field discovery, Assimp conventions, Opus frame/channel counts, packet loss and recovery, local source refresh
and dependency ordering:

```powershell
dotnet run --project tests/Resonator.Remapping.Tests -c Release -p:ResoniteDir="<install>"
```
