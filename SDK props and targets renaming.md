# nanoFramework SDK — naming consistency pass

## Context

The new `nanoFramework.NET.Sdk` (on the `move-to-sdk` branch) carries its build logic in
seven MSBuild files under [nanoFramework.NET.Sdk/Sdk/](nanoFramework.NET.Sdk/Sdk/).
Those files accreted **four** different naming styles for symbols and properties:

- `_NfXxx` — underscore + the `Nf` abbreviation (internal paths)
- `NanoXxx` — `Nano` PascalCase (the established public style)
- `NanoFramework_Xxx` — `NanoFramework` + an underscore separator (unusual for MSBuild)
- `NFMDP_XXX_Yyy` — SCREAMING_SNAKE with the `NF`/`MDP` abbreviations (carried over from the
  legacy NFProjectSystem targets)

Plus a casing slip: the target `MetaDataProcessor` (should be `Metadata`, one word). The legacy
`IsCoreAssembly` and `NanoIsCoreLibrary` were originally treated as the same concept, but they are
not (see section D): `NanoIsCoreLibrary` is the broad core-library flag, while the mscorlib-only
build mechanics live on a separate `NanoIsMscorlib` flag.

The goal is a single, descriptive, .NET-SDK-consistent convention without breaking anyone:

- **Public/user-settable properties:** `Nano` PascalCase prefix, no underscores, no SCREAMING_SNAKE.
- **Internal/computed symbols:** leading-underscore `_Nano…` PascalCase (mirrors the .NET SDK's
  `_OutputPathWasMissing` / `_TargetFrameworkDirectories` style).
- **Acronyms:** PascalCase per the .NET Framework Design Guidelines → `Tfm`, `Mdp`, `Pe`, `Dat`,
  `Xml`, `Pdbx`. (Matches the existing `nanoFramework.Tfm.props` filename.)
- **Legacy user knobs:** rename to the new name **but keep the old name as a fallback alias** so
  existing `.csproj` files keep working.

All current references are confined to the seven `Sdk/` files (verified by repo-wide grep), so
renames are local — except the external contracts listed in "Do NOT rename" below.

## Rename tables

### A. Internal SDK path properties (clean rename)

| Current | Proposed |
|---|---|
| `_NfSdkDir` | `_NanoSdkDir` |
| `_NfSdkRoot` | `_NanoSdkRoot` |
| `_NfSdkTasksDir` | `_NanoBuildTasksDir` (the SDK-bundled `BuildTasks.dll` dir — distinct from the MDP NuGet dir) |
| `_NfMdpTasksTFM` | `_NanoMdpTasksTfm` |
| `_NfMdpTasksDir` | `_NanoMdpTasksDir` |

### B. Internal pipeline props/items (drop the `NanoFramework_` underscore separator → `_Nano…`)

| Current | Proposed |
|---|---|
| `NanoFramework_StartProgram` | `_NanoStartProgram` |
| `NanoFramework_IntermediateAssembly` | `_NanoIntermediateAssembly` |
| `NanoFramework_Assembly` | `_NanoOutputAssembly` |
| `@(NanoFramework_Resources)` | `@(_NanoResources)` |
| `@(NanoFramework_StartProgram_ResolvedFiles)` | `@(_NanoStartProgramResolvedFiles)` |
| `@(NanoFramework_StartProgram_ResolvedDependencyFiles)` | `@(_NanoStartProgramResolvedDependencyFiles)` |

### C. Legacy `NFMDP_*` symbols

**C1 — user-facing knobs → rename + alias** (see alias pattern below):

| Current | Proposed (primary) |
|---|---|
| `NFMDP_PE_Verbose` | `NanoMdpVerbose` |
| `NFMDP_PE_VerboseMinimize` | `NanoMdpVerboseMinimize` |

**C2 — public project-file switches:**

| Current | Proposed |
|---|---|
| `_NanoMdpDumpMetadata` | `NanoMdpDumpMetadata` |
| `_NanoMdpGenerateStubs` | `NanoMdpGenerateStubs` |
| `_NanoMdpGenerateDatFiles` | `NanoMdpGenerateDatFiles` |
| `_NanoMdpGenerateXmlFiles` | `NanoMdpGenerateXmlFiles` |

**C3 — internal core-assembly path computations + stub params → `_NanoMdp…` / `_NanoMdpStub…`:**

| Current | Proposed |
|---|---|
| `NFMDP_PE_Parse` | `_NanoMdpParseInput` |
| `NFMDP_PE_Compile` | `_NanoMdpCompileOutput` |
| `NFMDP_PE_Compile_NoExt` | `_NanoMdpCompileOutputNoExt` |
| `NFMDP_PE_SaveStrings` | `_NanoMdpSaveStringsFile` |
| `NFMDP_PE_DumpExports` | `_NanoMdpDumpExportsFile` |
| `NFMDP_PE_GenerateDependency` | `_NanoMdpDependencyMapFile` |
| `@(NFMDP_PE_LoadHints)` | `@(_NanoMdpLoadHints)` |
| `NFMDP_STUB_GenerateSkeletonProject` | `_NanoMdpStubSkeletonProject` |
| `NFMDP_STUB_GenerateSkeletonName` | `_NanoMdpStubSkeletonName` |
| `NFMDP_STUB_GenerateSkeletonFile` | `_NanoMdpStubSkeletonFile` |
| `NFMDP_STUB_SkeletonWithoutInterop` | `_NanoMdpStubSkeletonWithoutInterop` |
| `NFMDP_STUB_Resolve` | `_NanoMdpStubResolve` |
| `NFMDP_STUB_DumpExports` *(referenced, never defined)* | `_NanoMdpStubDumpExports` |
| `NFMDP_STUB_GenerateDependency` *(referenced, never defined)* | `_NanoMdpStubGenerateDependency` |

### D. Already-`Nano` public knobs — acronym/case tweaks (+ alias on the two renamed)

| Current | Proposed | Alias kept? |
|---|---|---|
| `NanoFrameworkMDPVersion` | `NanoMdpVersion` | yes |
| `NanoIsCoreLibrary` | unchanged | — |
| `IsCoreAssembly` | `NanoIsMscorlib` (mscorlib-only mechanics; see below) | no |
| `NanoGenerateStubsDirectory` / `…StubsRootName` / `…SkeletonProjectName` / `…SkeletonFile` | unchanged (already on-convention) | — |

`NanoIsCoreLibrary` and `NanoIsMscorlib` are **two distinct concepts**, not one:

- `NanoIsCoreLibrary` (legacy `NF_IsCoreLibrary`) — an official core library with native firmware
  support and **no Interop layer**. Drives the MDP `IsCoreLibrary` parameter, `SkeletonWithoutInterop`,
  and stub/dump/dependency/strings generation. Set by mscorlib **and** other native core libraries
  (e.g. `System.Device.Gpio`, `nanoFramework.System.Math`, `nanoFramework.Runtime.Native`).
- `NanoIsMscorlib` (legacy `IsCoreAssembly`) — the project builds **mscorlib itself**. Because
  mscorlib cannot reference itself, this suppresses the auto-generated assembly attributes that pull
  in `System.*` types and points Roslyn at `coreAssembly.rsp` for the `runtimemetadataversion`. These
  settings must NOT apply to other core libraries, which reference mscorlib normally.

Building mscorlib implies the core-library pipeline, so `NanoIsMscorlib == True` seeds
`NanoIsCoreLibrary` when the project has not set it explicitly. No legacy fallback aliases are kept;
projects migrate to the new names directly.

### E. Target names → PascalCase, `Nano` namespace, `Metadata` casing fix

| Current | Proposed |
|---|---|
| `MetaDataProcessor` | `NanoMetadataProcessor` |
| `MetaDataProcessorCompile` | `NanoMetadataProcessorCompile` |
| `MetaDataProcessorDat` | `NanoMetadataProcessorDat` |
| `MetaDataProcessorDependsOn` *(property)* | `NanoMetadataProcessorDependsOn` |
| `NFMDP_CreateDatabaseAndDependencyMap` | `_NanoCreateDatabaseAndDependencyMap` |
| `NanoFrameworkClean` | `NanoClean` |
| `CopyToOutDir` | `_NanoCopyAssemblyToOutDir` |
| `CopyNanoFrameworkFiles` | `NanoCopyOutputFiles` |
| `CopyBackNanoFrameworkDlls` | `_NanoCopyReferencesToIntermediate` |
| `NanoCLR_CleanExtraFiles` | `_NanoCleanExtraFiles` |
| `NanoResourceGenerator` | `NanoGenerateResources` (verb-first, like MS `GenerateResource`) |
| `NanoGenerateBinaryOutput` | unchanged |
| `ResolveRuntimeDependencies` | unchanged (matches MS dependency-target style; referenced in `…DependsOn` chains) |

Each renamed target name must also be updated where it appears in the `*DependsOn` property
chains and `<CallTarget>` calls (all within these same files).

### F. Do NOT rename (external / fixed contracts)

- **Env-var overrides** set by devs/CI: `NF_MSBUILDTASK_PATH`, `NF_MDP_MSBUILDTASK_PATH`.
- **CI output variable** consumed by external pipelines: `NF_NATIVE_ASSEMBLY_CHECKSUM`
  (and the harness-provided `TF_BUILD`, `GITHUB_ACTIONS`, `GITHUB_ENV`).
- **Compile `#if` symbols:** `NETNANO1_0`, `NANOFRAMEWORK_1_0`.
- **VS CPS / debugger contract:** `NanoDebugger` (DebuggerFlavor + `Rules\NanoDebugger.xaml`),
  `NanoCSharpProject`, `NanoDeployableProject`, `LaunchProfiles`.
- **NuGet-generated:** `PkgnanoFramework_Tools_MetadataProcessor_MsBuildTask`.
- **C# task type & parameter names:** `MetaDataProcessorTask`, `ResolveRuntimeDependenciesTask`,
  `GenerateBinaryOutputTask`, `GenerateNanoResourceTask`, and params like `Verbose`, `Parse`,
  `Compile`, `IsCoreLibrary`, `GenerateSkeletonFile` (fixed by the task classes in
  `nanoFramework.Tools.BuildTasks`).
- **Standard MSBuild/.NET-SDK symbols:** `DebugType`, `OutDir`, `TargetName`,
  `IntermediateOutputPath`, and the standard hook chains `CoreBuildDependsOn`,
  `PrepareForRunDependsOn`, `PrepareResourcesDependsOn`, `ResolveReferencesDependsOn`,
  `CleanDependsOn`, etc.

## Back-compat alias pattern (for the C1 + D renamed knobs)

For each renamed user knob, seed the new property from the legacy one before applying its default,
so an existing `.csproj` setting still wins:

```xml
<PropertyGroup>
  <!-- Back-compat: honor the legacy NFMDP_PE_Verbose if the project still sets it -->
  <NanoMdpVerbose Condition="'$(NanoMdpVerbose)' == '' and '$(NFMDP_PE_Verbose)' != ''">$(NFMDP_PE_Verbose)</NanoMdpVerbose>
  <NanoMdpVerbose Condition="'$(NanoMdpVerbose)' == ''">false</NanoMdpVerbose>
</PropertyGroup>
```

Same shape for `NanoMdpVerboseMinimize` ← `NFMDP_PE_VerboseMinimize`, `NanoMdpVersion` ←
and `NanoFrameworkMDPVersion`. (`NanoIsCoreLibrary` / `NanoIsMscorlib` keep no legacy fallback.)

## Files touched

All under [nanoFramework.NET.Sdk/Sdk/](nanoFramework.NET.Sdk/Sdk/):
`Sdk.props`, `Sdk.targets`, `nanoFramework.Tfm.props`, `nanoFramework.Mdp.targets`,
`nanoFramework.Output.targets`, `nanoFramework.Resources.targets`. `test/SmokeTest/SmokeTest.csproj`
was also updated to use the public `NanoMdpGenerateStubs` name. (`nanoFramework.Capabilities.targets`
needs no change — its symbols are all the fixed CPS contract.) Update the explanatory header
comments that mention the old names (e.g. the `_NfSdkTasksDir` / `NanoFramework_*` / `_NfMdpTasksTFM`
references in the Sdk.targets and Mdp.targets banners).

## Observations surfaced during review (not renames — confirm intent separately)

- In the regular-project `MetaDataProcessor` target, `DumpExports="$(NFMDP_STUB_DumpExports)"` and
  `GenerateDependency="$(NFMDP_STUB_GenerateDependency)"` reference properties that are **never
  defined**, so they always pass empty. Likely intended to be the `NFMDP_PE_*` equivalents. Flagged
  for a follow-up decision; the rename keeps current behavior (empty) unless you confirm a fix.

## Verification

1. Pack/build the SDK: `dotnet build nanoFramework.NET.Sdk/nanoFramework.NET.Sdk.csproj`.
2. Build the smoke test against the local SDK:
   `dotnet build test/SmokeTest/SmokeTest.csproj -c Debug` — confirm it restores the MDP package,
   runs `NanoMetadataProcessor`, and emits `SmokeTest.pe` + `SmokeTest.pdbx` under
   `bin/Debug/netnano1.0/` (this exercises the regular-project path + resource gen + binary output +
   copy targets).
3. Run a clean: `dotnet build test/SmokeTest/SmokeTest.csproj -t:Clean` — confirm `NanoClean`
   removes the `.pe`/`.pdbx`.
4. Set a renamed knob's **legacy** name in a quick test (`-p:NFMDP_PE_Verbose=true`) and confirm the
   alias still flows through to verbose MDP output — proving back-compat.
5. Open `test/SmokeTest` in Visual Studio and confirm F5 still routes to the NanoDebugger flavor
   (the untouched Capabilities/CPS contract).

The core-assembly (`NanoIsCoreLibrary == true`) path is only exercised by the external mscorlib
repo; review those renames by inspection since this repo has no core-assembly test project.
