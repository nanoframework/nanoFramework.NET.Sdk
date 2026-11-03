# Copilot instructions

## Build and verification

Run these commands from the repository root:

```powershell
dotnet build nanoFramework.NET.Sdk.sln -c Release
dotnet test test\BuildTasks.Tests\BuildTasks.Tests.csproj -f net8.0
dotnet test test\BuildTasks.Tests\BuildTasks.Tests.csproj -f net8.0 --filter "FullyQualifiedName~nanoFramework.Tools.BuildTasks.Tests.Rgb565ConversionTests.Convert_PureRed_YieldsExpectedValue"
```

The test project targets both `net8.0` and `net472`. Run the `net472` target on Windows to exercise the full-framework resource-processing integration tests:

```powershell
dotnet test test\BuildTasks.Tests\BuildTasks.Tests.csproj -f net472
```

For an SDK-package smoke test, pack the SDK first, then build the sample project:

```powershell
dotnet pack nanoFramework.NET.Sdk\nanoFramework.NET.Sdk.csproj -c Release
dotnet build test\SmokeTest\SmokeTest.csproj -c Debug
```

`test\SmokeTest\Verify-Parity.ps1` compares generated `.nanoresources` files from `dotnet`/`net8.0` and Visual Studio MSBuild/`net472`; it requires Windows MSBuild.

## Architecture

- `nanoFramework.NET.Sdk` is a packaging project, not the SDK implementation in C#. It packages the `Sdk` props/targets plus the custom task assemblies from `nanoFramework.Tools.BuildTasks` for `net8.0` (dotnet MSBuild) and `net472` (Visual Studio MSBuild).
- `Sdk\Sdk.props` imports `Microsoft.NET.Sdk` and then `nanoFramework.Tfm.props`, which establishes `netnano1.0` and prevents standard framework references from leaking into nanoFramework compilation. `Sdk\Sdk.targets` imports the Microsoft targets and then the focused modules in dependency order: MDP, resources, output, and Visual Studio CPS/debugger capabilities.
- The main build pipeline compiles managed IL first, then `nanoFramework.Mdp.targets` invokes MetadataProcessor after `CoreCompile` to produce the nanoFramework `.pe` and `.pdbx`. `nanoFramework.Resources.targets` removes `.resx` items from the standard resource pipeline and generates `.nanoresources` for MetadataProcessor. `nanoFramework.Output.targets` generates deployable output, copies artifacts, reports the native checksum, and handles cleanup.
- Build tasks target `net8.0;net472`. The modern .NET resource path uses the cross-platform implementations in `ProcessResourceFiles.CrossPlatform.cs` (ImageSharp and CodeDom); the .NET Framework path uses `System.Drawing`/WinForms. Tests are guarded by target: parser/bitmap tests run on `net8.0`, while `ProcessResourceFilesTests` runs on `net472`.

## Repository-specific conventions

- Keep `Sdk.props` and `Sdk.targets` as thin orchestrators. Put SDK logic in the focused module for that concern, and preserve the import order: the resource and output modules use task paths and properties established by the MDP module.
- Extend standard MSBuild `*DependsOn` properties rather than replacing them. The `.pe` generation stage is ordered with `AfterTargets="CoreCompile"` so it runs after managed compilation and before the output-copy stage.
- Use the `Nano` PascalCase prefix for public, project-settable properties and `_Nano` for internal/computed properties. Preserve legacy property aliases where the SDK already provides them. Names such as `NF_MSBUILDTASK_PATH`, `NF_MDP_MSBUILDTASK_PATH`, `NF_NATIVE_ASSEMBLY_CHECKSUM`, debugger/CPS capabilities, and task parameter names are external contracts; verify consumers before changing them.
- When changing resource processing, preserve parity between `net8.0` and `net472`; use the smoke-test parity script in addition to focused tests where applicable. If adding a dependency to the `net8.0` build task, ensure it is copied beside and packaged with the task DLL by the BuildTasks and SDK packaging projects.
