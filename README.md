# nanoFramework.Sdk

MSBuild SDK package for .NET nanoFramework projects. Enables building nanoFramework class libraries and applications using `dotnet build` and standard SDK-style project files.

## Overview

This SDK replaces the legacy MSBuild infrastructure previously bundled inside the Visual Studio extension (VSIX). It packages the nanoFramework build pipeline — C# compilation, metadata processing (IL→PE), resource generation, and binary output — into a standard NuGet-distributed MSBuild SDK.

## Usage

Create an SDK-style project file (`.csproj`):

```xml
<Project Sdk="nanoFramework.Sdk/0.1.0">

  <PropertyGroup>
    <OutputType>Library</OutputType>
    <RootNamespace>MyLibrary</RootNamespace>
    <AssemblyName>MyLibrary</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="nanoFramework.CoreLibrary" Version="1.17.11" />
  </ItemGroup>

</Project>
```

Build with the `dotnet` CLI:

```bash
dotnet build
dotnet pack
```

Or pin the SDK version in `global.json`:

```json
{
  "msbuild-sdks": {
    "nanoFramework.Sdk": "0.1.0"
  }
}
```

## Project Structure

```
src/
  nanoFramework.NET.Sdk/           SDK NuGet package (Sdk.props / Sdk.targets)
  nanoFramework.Tools.BuildTasks/  Custom MSBuild tasks (resource gen, binary output, etc.)
```

## Tools

The `dotnet nano` umbrella CLI and the `nano-migrate` project migrator (with the companion
SDK-migration skill) live in the [nanoframework/nf-tools](https://github.com/nanoframework/nf-tools)
repository, under `tools/nano` and `tools/migrate`.

## Development

```bash
dotnet build
dotnet pack src\nanoFramework.NET.Sdk\nanoFramework.NET.Sdk.csproj
```

The resulting `.nupkg` can be tested locally by adding its output directory as a NuGet source.

## License

See LICENSE file for details.
