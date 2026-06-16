using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NanoFramework.Migrate;

/// <summary>How a single project ended up after a (dry-run or real) conversion.</summary>
internal enum ConvertStatus
{
    Converted,   // converted cleanly
    Skipped,     // already SDK-style; nothing to do
    Review,      // converted, but flagged items need a human
    Error,       // threw while converting
}

/// <summary>Outcome of converting a single project.</summary>
internal sealed class ConvertResult
{
    public required string OutputPath { get; init; }
    public List<string> Review { get; } = new();

    /// <summary>True when the project was already SDK-style and was left untouched.</summary>
    public bool AlreadySdk { get; set; }

    /// <summary>Resolved PackageReferences (id -> version) the emitted project will carry.</summary>
    public List<KeyValuePair<string, string>> Packages { get; } = new();

    /// <summary>Files this conversion deletes (or, in dry-run, would delete).</summary>
    public List<string> DeletedFiles { get; } = new();

    /// <summary>.sln files this conversion retargets (or, in dry-run, would retarget).</summary>
    public List<string> UpdatedSolutions { get; } = new();

    /// <summary>Set when the conversion threw; used to render a red Error row.</summary>
    public string? Error { get; set; }

    public ConvertStatus Status =>
        Error is not null ? ConvertStatus.Error
        : AlreadySdk       ? ConvertStatus.Skipped
        : Review.Count > 0 ? ConvertStatus.Review
        :                    ConvertStatus.Converted;
}

/// <summary>
/// Converts one legacy .nfproj into an SDK-style project. Faithful to the
/// reference rules: drop project-system boilerplate and SDK-supplied defaults,
/// fold packages.config into PackageReference, fold .nuspec metadata into Pack
/// properties, drop default Compile globs and a hand-written AssemblyInfo.cs,
/// and "fail loud" — anything it cannot confidently convert is surfaced for a
/// human rather than guessed.
/// </summary>
internal static class Converter
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/developer/msbuild/2003";

    // Project-system boilerplate and properties the SDK now supplies itself.
    private static readonly HashSet<string> DropProps = new(StringComparer.Ordinal)
    {
        "ProjectTypeGuids", "ProjectGuid", "FileAlignment", "AppDesignerFolder",
        "NanoFrameworkProjectSystemPath", "TargetFrameworkVersion", "OldToolsVersion",
        "Configuration", "Platform",
    };

    // Properties carried through verbatim when present.
    private static readonly HashSet<string> KeepProps = new(StringComparer.Ordinal)
    {
        "RootNamespace", "AssemblyName", "DocumentationFile", "DefineConstants", "LangVersion",
        "Description", "Authors", "PackageTags", "Copyright",
    };

    // Legacy <Reference Include="X"> names whose NuGet package id differs from X.
    private static readonly Dictionary<string, string> LegacyPkgAliases = new(StringComparer.Ordinal)
    {
        ["mscorlib"] = "nanoFramework.CoreLibrary",
        ["System"]   = "nanoFramework.CoreLibrary",
    };

    // Matches a NuGet "packages\<Id>.<Version>\" folder segment inside a HintPath.
    // <id> is greedy-then-split: the version is the tail starting at the first
    // dotted segment that begins with a digit, so prerelease/build suffixes
    // (e.g. "2.0.0-preview.52") stay attached to the version, not the id.
    private static readonly Regex HintPathPackage = new(
        @"[\\/]packages[\\/](?<folder>[^\\/]+)[\\/]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The SDK reference is versionless; the concrete version is pinned via a
    // global.json `msbuild-sdks` entry, not the Sdk attribute.
    private const string SdkReference = "nanoFramework.NET.Sdk";

    public static ConvertResult Convert(string nfproj, Options o)
    {
        var projDir = Path.GetDirectoryName(Path.GetFullPath(nfproj))!;
        var root = XElement.Load(nfproj);

        // Idempotency guard: an SDK-style project already has an Sdk attribute on
        // the root. Treat it as already-converted and skip without touching disk,
        // so a second run over a repo is a true no-op rather than destructive.
        if (root.Attribute("Sdk") is not null)
        {
            var skipped = new ConvertResult { OutputPath = Path.GetFullPath(nfproj), AlreadySdk = true };
            skipped.Review.Add("already SDK-style; skipped");
            return skipped;
        }

        var pkgs = LoadPackagesConfig(projDir);

        var props = new List<KeyValuePair<string, string>>();   // discovery order, deduped
        var pkgRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projRefs = new List<string>();
        var keepItems = new List<XElement>();
        var review = new List<string>();

        void SetProp(string k, string? v)
        {
            if (string.IsNullOrEmpty(v)) return;
            if (props.Any(p => p.Key == k)) return;
            props.Add(new(k, v));
        }

        // Read by local name so the converter works whether the input uses the
        // legacy MSBuild namespace or none at all.
        foreach (var pg in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            foreach (var el in pg.Elements())
            {
                var tag = el.Name.LocalName;
                if (DropProps.Contains(tag)) continue;
                if (KeepProps.Contains(tag)) SetProp(tag, el.Value);
            }

        foreach (var ig in root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
            foreach (var el in ig.Elements())
            {
                var tag = el.Name.LocalName;
                var inc = (string?)el.Attribute("Include") ?? "";
                switch (tag)
                {
                    case "Reference":
                    {
                        var rawName = inc.Split(',')[0].Trim();
                        // Prefer id+version parsed straight from the HintPath folder.
                        var fromHint = InferFromHintPath(el);
                        if (fromHint is not null)
                        {
                            pkgRefs[fromHint.Value.id] = fromHint.Value.ver;
                            break;
                        }
                        // Fallback: resolve the package id via the alias table, then
                        // look up its version in packages.config.
                        var name = LegacyPkgAliases.GetValueOrDefault(rawName, rawName);
                        var ver = pkgs.GetValueOrDefault(name) ?? pkgs.GetValueOrDefault(rawName);
                        if (ver is not null) pkgRefs[name] = ver;
                        else
                            review.Add($"Reference without resolvable version: {inc} "
                                     + "(no HintPath or packages.config entry; map to a PackageReference manually)");
                        break;
                    }
                    case "PackageReference":
                    {
                        var ver = (string?)el.Attribute("Version") ?? pkgs.GetValueOrDefault(inc);
                        if (ver is not null) pkgRefs[inc] = ver;
                        else
                            review.Add($"PackageReference without resolvable version: {inc} "
                                     + "(add a Version manually)");
                        break;
                    }
                    case "ProjectReference":
                        projRefs.Add(inc);
                        break;
                    case "Compile":
                        if (!IsDefaultCompile(inc) || el.Attribute("Link") is not null)
                            keepItems.Add(el);
                        break;
                    case "None":
                        if (inc != "packages.config" && !inc.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                            keepItems.Add(el);
                        break;
                    case "EmbeddedResource":
                    case "Content":
                        keepItems.Add(el);
                        break;
                    default:
                        review.Add($"Unhandled item <{tag} Include='{inc}'>");
                        break;
                }
            }

        FoldNuspec(projDir, SetProp);

        var xml = Emit(props, pkgRefs, projRefs, keepItems, o);

        var outPath = Path.ChangeExtension(Path.GetFullPath(nfproj), o.Ext);
        var nfFull = Path.GetFullPath(nfproj);
        var replacingNfproj = !string.Equals(outPath, nfFull, StringComparison.OrdinalIgnoreCase);

        var result = new ConvertResult { OutputPath = outPath };
        result.Review.AddRange(review);
        result.Packages.AddRange(pkgRefs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase));

        // Compute the set of files that will be (or, in dry-run, would be) removed
        // and the solutions that will be retargeted. This drives the dry-run
        // preview and is identical to what the real run acts on.
        if (replacingNfproj) result.DeletedFiles.Add(nfFull);
        var pc = Path.Combine(projDir, "packages.config");
        if (File.Exists(pc)) result.DeletedFiles.Add(Path.GetFullPath(pc));
        foreach (var ai in ExistingAssemblyInfo(projDir)) result.DeletedFiles.Add(ai);
        if (replacingNfproj)
            foreach (var sln in SolutionsReferencing(projDir, nfFull)) result.UpdatedSolutions.Add(sln);

        if (!o.DryRun)
        {
            // Never clobber an existing backup: the original first-run .bak must
            // survive reruns.
            if (!o.NoBackup && !File.Exists(nfproj + ".bak")) File.Copy(nfproj, nfproj + ".bak", overwrite: false);
            File.WriteAllText(outPath, xml, new UTF8Encoding(false));
            // If we emitted a .csproj alongside, retire the original .nfproj.
            if (replacingNfproj)
            {
                File.Delete(nfproj);
                // Point any .sln entries at the new .csproj (idempotent).
                UpdateSolutions(projDir, nfproj, outPath);
            }
            if (File.Exists(pc)) File.Delete(pc);
            // The SDK default **/*.cs glob plus generated assembly info would
            // otherwise produce duplicate-attribute build errors, so delete a
            // hand-written AssemblyInfo.cs from disk (dropping the Compile item
            // is not enough).
            DeleteAssemblyInfo(projDir);
        }

        return result;
    }

    // Paths of any hand-written AssemblyInfo.cs that exist on disk (the files
    // DeleteAssemblyInfo would remove). Used to preview deletions in dry-run.
    private static IEnumerable<string> ExistingAssemblyInfo(string projDir)
    {
        foreach (var rel in new[] { Path.Combine("Properties", "AssemblyInfo.cs"), "AssemblyInfo.cs" })
        {
            var path = Path.Combine(projDir, rel);
            if (File.Exists(path)) yield return Path.GetFullPath(path);
        }
    }

    // The .sln files that currently still reference the .nfproj (i.e. those
    // UpdateSolutions would rewrite). Used to preview .sln edits in dry-run.
    private static IEnumerable<string> SolutionsReferencing(string projDir, string nfproj)
    {
        var nfName = Path.GetFileName(nfproj);
        foreach (var sln in FindSolutionFiles(projDir))
        {
            string text;
            try { text = File.ReadAllText(sln); }
            catch { continue; }
            if (text.Contains(nfName, StringComparison.OrdinalIgnoreCase))
                yield return sln;
        }
    }

    // Parses the "packages\<Id>.<Version>\" folder segment of a HintPath into a
    // (id, version) pair. The version is the suffix that begins at the first
    // dotted segment starting with a digit; everything before it is the id.
    private static (string id, string ver)? InferFromHintPath(XElement reference)
    {
        var hint = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
        if (string.IsNullOrEmpty(hint)) return null;
        var m = HintPathPackage.Match(hint);
        if (!m.Success) return null;
        return SplitPackageFolder(m.Groups["folder"].Value);
    }

    // "nanoFramework.System.Device.Gpio.1.1.57" -> ("nanoFramework.System.Device.Gpio", "1.1.57")
    // "nanoFramework.CoreLibrary.2.0.0-preview.52" -> ("nanoFramework.CoreLibrary", "2.0.0-preview.52")
    private static (string id, string ver)? SplitPackageFolder(string folder)
    {
        var parts = folder.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && char.IsDigit(parts[i][0]))
            {
                if (i == 0) return null; // no id before the version
                var id = string.Join('.', parts[..i]);
                var ver = string.Join('.', parts[i..]);
                return (id, ver);
            }
        }
        return null; // no version segment found
    }

    private static Dictionary<string, string> LoadPackagesConfig(string projDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pc = Path.Combine(projDir, "packages.config");
        if (!File.Exists(pc)) return result;
        foreach (var p in XElement.Load(pc).Elements().Where(e => e.Name.LocalName == "package"))
        {
            var id = (string?)p.Attribute("id");
            var ver = (string?)p.Attribute("version");
            if (id is not null && ver is not null) result[id] = ver;
        }
        return result;
    }

    // Removes a hand-written AssemblyInfo.cs from disk. With the SDK's default
    // **/*.cs glob and GenerateAssemblyInfo, leaving it would cause duplicate
    // assembly-attribute build errors. Idempotent: a missing file is a no-op.
    private static void DeleteAssemblyInfo(string projDir)
    {
        foreach (var rel in new[] { Path.Combine("Properties", "AssemblyInfo.cs"), "AssemblyInfo.cs" })
        {
            var path = Path.Combine(projDir, rel);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Project-type GUIDs: legacy nanoFramework flavor -> SDK-style C#.
    private const string NfprojTypeGuid = "{11A8DD76-328B-46DF-9F39-F559912D0360}";
    private const string CsprojTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";

    // Rewrites .sln entries that reference the converted .nfproj so they point at
    // the new .csproj and use the SDK-style project-type GUID. Searches walking up
    // from the project directory to the repo root (the dir containing .git), plus
    // any .sln in the project's own directory tree. Only entries still pointing at
    // the .nfproj are touched, so re-running is a no-op.
    private static void UpdateSolutions(string projDir, string nfproj, string csproj)
    {
        var slns = FindSolutionFiles(projDir);
        if (slns.Count == 0) return;

        var nfName = Path.GetFileName(nfproj);                 // e.g. Foo.nfproj
        var csName = Path.GetFileName(csproj);                 // e.g. Foo.csproj

        foreach (var sln in slns)
        {
            string text;
            try { text = File.ReadAllText(sln); }
            catch { continue; }
            var slnDir = Path.GetDirectoryName(Path.GetFullPath(sln))!;

            // The path stored in the .sln is relative to the .sln directory and is
            // wrapped in double quotes. Match the *quoted* path so we rewrite the
            // specific project entry rather than blanket-replacing a substring that
            // might collide with another project's name.
            var relPath = Path.GetRelativePath(slnDir, nfproj);
            var candidates = new[]
            {
                relPath,                              // OS-native separators
                relPath.Replace('/', '\\'),
                relPath.Replace('\\', '/'),
                nfName,                               // fallback: bare filename in the sln dir
            };

            // Rewrite line-by-line so the GUID swap stays scoped to *this*
            // project's entry. A blanket GUID replace would wrongly flip other,
            // still-unconverted nanoFramework projects in a shared solution.
            var lines = text.Split('\n');
            var changed = false;
            for (int li = 0; li < lines.Length; li++)
            {
                var line = lines[li];
                // Only the project's declaration line names its path.
                if (!line.TrimStart().StartsWith("Project(", StringComparison.OrdinalIgnoreCase)) continue;

                var matched = false;
                foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var quoted = "\"" + path + "\"";
                    if (!line.Contains(quoted, StringComparison.OrdinalIgnoreCase)) continue;
                    var quotedReplacement = "\"" + path[..^nfName.Length] + csName + "\"";
                    line = ReplaceIgnoreCase(line, quoted, quotedReplacement);
                    matched = true;
                }
                if (!matched) continue;

                // Swap the legacy project-type GUID for the SDK-style one, but only
                // on the line we just retargeted.
                if (line.Contains(NfprojTypeGuid, StringComparison.OrdinalIgnoreCase))
                    line = ReplaceIgnoreCase(line, NfprojTypeGuid, CsprojTypeGuid);

                lines[li] = line;
                changed = true;
            }
            if (!changed) continue;

            var updated = string.Join('\n', lines);
            if (!string.Equals(updated, text, StringComparison.Ordinal))
                File.WriteAllText(sln, updated, new UTF8Encoding(false));
        }
    }

    private static List<string> FindSolutionFiles(string projDir)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Walk up to the repo root (the directory containing .git), collecting
        // .sln files at each level.
        var dir = new DirectoryInfo(Path.GetFullPath(projDir));
        while (dir is not null)
        {
            foreach (var sln in Directory.EnumerateFiles(dir.FullName, "*.sln", SearchOption.TopDirectoryOnly))
                found.Add(Path.GetFullPath(sln));
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git")))
                break; // reached the repo root
            dir = dir.Parent;
        }

        // Also any .sln anywhere in the project's own directory tree.
        foreach (var sln in Directory.EnumerateFiles(projDir, "*.sln", SearchOption.AllDirectories))
            found.Add(Path.GetFullPath(sln));

        return found.ToList();
    }

    private static string ReplaceIgnoreCase(string input, string search, string replacement)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < input.Length)
        {
            var idx = input.IndexOf(search, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { sb.Append(input, i, input.Length - i); break; }
            sb.Append(input, i, idx - i).Append(replacement);
            i = idx + search.Length;
        }
        return sb.ToString();
    }

    private static bool IsDefaultCompile(string inc)
    {
        var baseName = inc.TrimStart('.', '\\');
        if (!inc.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
        // A hand-written AssemblyInfo.cs collides with GenerateAssemblyInfo → drop it.
        if (baseName.Replace('\\', '/').EndsWith("Properties/AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            return true;
        return !baseName.Contains('\\');
    }

    private static void FoldNuspec(string projDir, Action<string, string?> setProp)
    {
        var nuspec = Directory.EnumerateFiles(projDir, "*.nuspec").FirstOrDefault();
        if (nuspec is null) return;
        var meta = XElement.Load(nuspec).Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (meta is null) return;
        foreach (var (xml, msb) in new[]
        {
            ("id", "PackageId"), ("description", "Description"), ("authors", "Authors"),
            ("tags", "PackageTags"), ("projectUrl", "PackageProjectUrl"),
        })
        {
            var e = meta.Elements().FirstOrDefault(x => x.Name.LocalName == xml);
            if (e is not null && !string.IsNullOrEmpty(e.Value)) setProp(msb, e.Value);
        }
    }

    private static string Emit(
        List<KeyValuePair<string, string>> props,
        Dictionary<string, string> pkgRefs,
        List<string> projRefs,
        List<XElement> keepItems,
        Options o)
    {
        var sb = new StringBuilder();
        // Versionless SDK reference; the version is pinned via global.json msbuild-sdks.
        sb.Append($"<Project Sdk=\"{SdkReference}\">\n\n");
        sb.Append("  <PropertyGroup>\n");
        sb.Append($"    <TargetFramework>{o.Tfm}</TargetFramework>\n");
        foreach (var kv in props)
            sb.Append($"    <{kv.Key}>{Escape(kv.Value)}</{kv.Key}>\n");
        sb.Append("  </PropertyGroup>\n\n");

        if (pkgRefs.Count > 0)
        {
            sb.Append("  <ItemGroup>\n");
            foreach (var kv in pkgRefs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append($"    <PackageReference Include=\"{kv.Key}\" Version=\"{kv.Value}\" />\n");
            sb.Append("  </ItemGroup>\n\n");
        }
        if (projRefs.Count > 0)
        {
            sb.Append("  <ItemGroup>\n");
            foreach (var r in projRefs)
                sb.Append($"    <ProjectReference Include=\"{Escape(r)}\" />\n");
            sb.Append("  </ItemGroup>\n\n");
        }
        if (keepItems.Count > 0)
        {
            sb.Append("  <ItemGroup>\n");
            foreach (var el in keepItems)
            {
                var attrs = string.Join(" ", el.Attributes().Select(a => $"{a.Name.LocalName}=\"{Escape(a.Value)}\""));
                sb.Append($"    <{el.Name.LocalName} {attrs} />\n");
            }
            sb.Append("  </ItemGroup>\n\n");
        }
        sb.Append("</Project>\n");
        return sb.ToString();
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
