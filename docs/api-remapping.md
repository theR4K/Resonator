# API remapping

The pipeline can remap bound original C# API symbols after automatic decompiler fixes and before patches.
Both ProjectBuilder and PatchGenerator's fresh baseline invoke ApiRemapStage through FixupStage.
`--stage remap` runs it alone on an existing generated source tree.

Targets configure `"api_remaps": ["remaps/library.json"]`. Paths are relative to the declaring target;
profiles accumulate root first through inheritance and stay enabled when generating patch baselines.
Profiles select source files with `files` globs and identify types/members by original assembly name
and C# documentation ID. Full generated source participates in semantic binding; only selected files change.

```json
{
  "files": ["Importer.cs"],
  "types": [
    { "assembly": "AssimpNet", "from": "Assimp.Vector3D", "to": "System.Numerics.Vector3" },
    { "assembly": "AssimpNet", "from": "Assimp.Matrix4x4", "to": "System.Numerics.Matrix4x4" }
  ],
  "members": [
    {
      "assembly": "AssimpNet",
      "from": "M:Assimp.Matrix4x4.FromScaling(Assimp.Vector3D)",
      "to": "global::System.Numerics.Matrix4x4.CreateScale",
      "kind": "call"
    }
  ]
}
```

Member kinds: `member` changes a field/property name, `rename` changes a source method and its bound uses,
`call` redirects invocation to a static function, `constructor` maps construction, and `operator` redirects
a binary operator to a static function. `$target` selects the current assembly's source declarations.

Calls use `receiver: none` for static originals and `value` or `ref` for instance originals. Method groups,
reduced extension calls and compound-assignment receivers needing temporaries are rejected when adaptation
cannot preserve semantics. Constructor/call `argument_names` names destination parameters by original
parameter ordinal, retaining source expression evaluation order even when parameter order changes.

Binding uses the original shipped reference closure. Rules validate source symbols, destinations, argument
names and method signature collisions before writing a profile's edits. Type replacements use fully qualified
names; comments and string literals are not textual replacement targets. The final compiler checks the full API.

`support_files` can copy managed adapter code from profile-relative `source` to generated-root-relative
`destination` paths. Paths stay within their roots; destinations must be C# files outside keep directories
and build output. Support files participate in binding but are not remapped as inputs. Split targets skip
profiles with no selected files. Tree targets cannot use C# API remapping.

Run regression checks with `dotnet run --project tests/Resonator.Remapping.Tests -c Release`.
