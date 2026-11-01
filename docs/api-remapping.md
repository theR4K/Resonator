# API remapping

API remapping replaces mechanical wrapper-API patches with explicit, symbol-based rules. It runs after
automatic decompiler fixes and before patch layers. `FixupStage` invokes `ApiRemapStage` for both ProjectBuilder
and PatchGenerator's fresh baseline, so generated patches contain only changes beyond the mappings.
`--stage remap` runs the remapper alone on an existing source tree.

Add `"api_remaps": ["remaps/assimp.json"]` to a target. Paths are relative to that target directory;
profiles accumulate root ancestor first through `extends`. A patch baseline drops the current patch layer
but retains all mapping profiles, including the current target's. A split target with no matching input files skips
the profile. Tree targets do not support API remapping.

The first profile is [Flatweave's Assimp mapping](../targets/Flatweave/FrooxEngine/remaps/assimp.json).
It maps the original AssimpNet API to Numerics and the shared [Flatweave.Adapter](flatweave-adapter.md) library.
The Opus profile likewise maps the original codec types to that library. Bone lookup, missing enum values,
property `out` arguments and material texture enumeration remain importer patches.

## Profile format

```json
{
  "files": ["FrooxEngine/AssimpExtensions.cs", "FrooxEngine/ModelImporter.cs"],
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

- `files`: source-root-relative globs to transform, default `**/*.cs`. Keep dirs and build output are excluded.
  All generated C# files participate in binding, even when only selected files are rewritten.
- `assembly`: original assembly simple name. `$target` identifies declarations in the target's own source.
- Type `from`/`to`: full type names. Uses are replaced with fully qualified destination names; aliases, arrays and
  generic arguments are handled by the syntax/semantic APIs. This replaces uses, not type definitions.
- Member `from`: an original C# documentation ID (`F:` field, `M:` method; constructor `#ctor`, byref parameter `@`).
- `kind: member`: replace a field/property name. `kind: rename`: rename a source method and its bound uses;
  this resolves collisions such as color and vector overloads both becoming `Vector3`.
- `kind: call`: redirect a method invocation to a static destination. `receiver` defaults to `none`; `value` or
  `ref` inserts the original instance receiver as the first argument. Instance adapters require explicit receivers;
  method groups requiring adapters are rejected rather than guessed.
  Instance methods require `value` or `ref`; static methods require `none`. Reduced extension calls must use explicit static syntax.
  Call adapters currently support non-generic methods; generic type arguments in ordinary type uses are preserved.
- `kind: constructor`: replace a constructor's destination type. `argument_names`, when supplied, names each
  original parameter's destination parameter. Arguments stay in source evaluation order. Assimp's `(w,x,y,z)`
  constructor becomes `new Quaternion(w: ..., x: ..., y: ..., z: ...)`, preserving side effects and component order.
- `kind: operator`: redirect a binary operator to a static two-argument adapter. Compound assignments expand only
  for locals/parameters; receivers and indexers needing temporaries are rejected.
- `support_files`: managed adapter sources, relative to the profile directory, copied to source-root-relative
  destinations. Both paths must stay inside their respective roots. These files are generated in build and baseline
  runs alike. Destinations must be C# files outside keep directories and build output; support sources are not remapped as input.
  They do not become injection warnings or source patches.

Binding follows the original shipped assembly reference closure, with the running framework supplying framework
references. It deliberately does not bind against replacement packages or also reference the target DLL: doing so
would introduce duplicate type names or extension methods. There is no textual fallback for unresolved mapped symbols.
Rules validate external source members, destination types/members and call arity, named arguments, and source-method
signature collisions before a profile writes files. The final build validates the complete replacement API.

## Assimp conventions

Type compatibility alone does not establish mathematical equivalence. The original AssimpNet multiplication operator
computes the raw product `right * left`; its decomposition uses row lengths and negates all scale components for a
negative determinant. Quaternion normalization also differs from Numerics. These behaviours are explicit in
`Flatweave.Adapters.AssimpMath`, alongside column-vector translation/rotation adapters. They are checked against the original managed DLL.

This stage does not unify serialized engine types or promise zero-copy operations. Those need separate compatibility
analysis and measurements. Unmapped behavioural changes remain ordinary patches.

## Verification

```powershell
dotnet run --project tests/Resonator.Remapping.Tests -c Release -p:ResoniteDir="<install>"
```

The executable checks assembly identity, aliases and generic/array uses, overload collisions, member/constructor/operator
rewrites, expression evaluation order, idempotence and target inheritance. With the original `AssimpNet.dll` available,
it also compares matrix adapters against the original API without loading a native library. A clean target build followed
by PatchGenerator must still leave its patch layer unchanged.
