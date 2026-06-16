namespace NanoFramework.Migrate.Core;

/// <summary>
/// The knobs that drive a conversion. This is the engine-facing options type — it
/// is intentionally NOT the CLI settings type, so the engine carries no console or
/// command-line dependency. The CLI maps its parsed settings onto this record.
/// </summary>
public sealed record ConversionOptions
{
    /// <summary>
    /// Output extension for the emitted project. Either <c>.csproj</c> or
    /// <c>.nfproj</c>. Default <c>.csproj</c>: a normal run produces Foo.csproj and
    /// retires Foo.nfproj.
    /// </summary>
    public string Ext { get; init; } = ".csproj";

    /// <summary>Target framework moniker written into the emitted project.</summary>
    public string Tfm { get; init; } = "netnano1.0";

    /// <summary>
    /// Accepted for back-compat but no longer emitted: the SDK reference is
    /// versionless (the version is pinned via global.json <c>msbuild-sdks</c>).
    /// </summary>
    public string Sdk { get; init; } = "2.0.0";

    /// <summary>Analyse and preview only; write nothing to disk.</summary>
    public bool DryRun { get; init; }

    /// <summary>Don't write a <c>.nfproj.bak</c> alongside the converted project.</summary>
    public bool NoBackup { get; init; }

    /// <summary>
    /// Glob filter (relative to the input directory) selecting which <c>.nfproj</c>
    /// to convert. Null means "all <c>.nfproj</c> recursively" (the default).
    /// Supports <c>*</c>, <c>**</c> and <c>?</c>.
    /// </summary>
    public string? Glob { get; init; }
}
