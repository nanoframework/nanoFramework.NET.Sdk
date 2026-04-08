# nanoFramework SDK Naming Convention Analysis

## Existing .NET SDK Names

Microsoft's own SDKs follow the **`Microsoft.NET.Sdk*`** pattern:

| SDK | Usage |
|-----|-------|
| `Microsoft.NET.Sdk` | Base SDK for console/library projects |
| `Microsoft.NET.Sdk.Web` | ASP.NET Core web apps |
| `Microsoft.NET.Sdk.Worker` | Worker services |
| `Microsoft.NET.Sdk.Razor` | Razor projects |
| `Microsoft.NET.Sdk.BlazorWebAssembly` | Blazor WASM |
| `Microsoft.NET.Sdk.WindowsDesktop` | WPF/WinForms |

Third-party/ecosystem SDKs follow the **`<Org>.NET.Sdk`** pattern:

| SDK | Usage |
|-----|-------|
| `MSBuild.Sdk.Extras` | Multi-targeting helper (notable exception) |
| `Tizen.NET.Sdk` | Samsung Tizen .NET apps |

## Recommendation: **`nanoFramework.NET.Sdk`**

### Reasoning

1. **Convention alignment** — The `.NET.Sdk` suffix is the dominant pattern for platform-specific SDKs that build on top of the .NET ecosystem. `Tizen.NET.Sdk` is the closest analog: a hardware/platform-specific .NET flavor from a non-Microsoft org — exactly like nanoFramework.

2. **Signals .NET membership** — The `.NET.` infix clearly communicates that nanoFramework is part of the broader .NET family, which aligns with the project's identity as a **.NET** runtime for embedded devices.

3. **Discoverability** — Developers searching for `.NET.Sdk` packages on NuGet will naturally find it alongside other platform SDKs.

4. **`nanoFramework.Sdk` is ambiguous** — Without `.NET.` in the name, it could be mistaken for a general-purpose SDK unrelated to the .NET ecosystem (e.g., a native C/C++ toolchain for nano-scale devices).

### Usage in project files

<Project Sdk="nanoFramework.NET.Sdk">

This reads naturally alongside the familiar `Microsoft.NET.Sdk` and mirrors the `Tizen.NET.Sdk` precedent exactly.
