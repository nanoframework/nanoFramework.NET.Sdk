using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NanoFramework.Migrate.Core;

/// <summary>
/// Converts one legacy .nfproj into an SDK-style project. Faithful to the
/// reference rules: drop project-system boilerplate and SDK-supplied defaults,
/// fold packages.config into PackageReference, fold .nuspec metadata into Pack
/// properties, drop default Compile globs and a hand-written AssemblyInfo.cs,
/// and "fail loud" — anything it cannot confidently convert is surfaced for a
/// human rather than guessed.
/// </summary>
public sealed class ProjectConverter : IProjectConverter
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
        // nanoFramework unit-test projects: these keep the migrated project a test
        // project (the SDK does not infer them).
        "IsTestProject", "TestProjectType", "RunSettingsFilePath",
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

    public ConvertResult Convert(string nfprojPath, ConversionOptions options)
    {
        var nfproj = nfprojPath;
        var o = options;
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
        // packages.config — when present and non-empty — IS the authoritative
        // dependency list. In that mode legacy <Reference> assembly names are NOT
        // mapped to packages (their names routinely differ from the NuGet package
        // id, e.g. assembly "System.Net.Http" ships in package
        // "nanoFramework.System.Net.Http.Server"); the packages.config ids+versions
        // are taken verbatim instead, eliminating the name/id mismatch.
        var packagesConfigAuthoritative = pkgs.Count > 0;

        // Detect Central Package Management: an explicit option override wins;
        // otherwise walk up for a Directory.Packages.props with
        // ManagePackageVersionsCentrally=true.
        var cpmPropsPath = FindDirectoryPackagesProps(projDir);
        var cpmActive = o.CentralPackageManagement
            ?? (cpmPropsPath is not null && IsCpmEnabled(cpmPropsPath));

        var props = new List<KeyValuePair<string, string>>();   // discovery order, deduped
        var pkgRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projRefs = new List<string>();
        var keepItems = new List<XElement>();
        var review = new List<string>();

        // Seed package references straight from packages.config when it is the
        // authoritative source. Drop the legacy <Reference> elements entirely in
        // this mode (handled in the item loop below).
        if (packagesConfigAuthoritative)
            foreach (var kv in pkgs)
                pkgRefs[kv.Key] = kv.Value;

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
                        // packages.config is authoritative: it already carries the
                        // full dependency set, so legacy <Reference> assembly names
                        // are dropped rather than (mis)mapped to a package id.
                        if (packagesConfigAuthoritative) break;

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
                        // A versionless <PackageReference> is valid under CPM; carry
                        // it through (its version, if any, comes from the central
                        // props). Otherwise resolve a version from the attribute or
                        // packages.config; only flag when truly unresolvable.
                        var ver = (string?)el.Attribute("Version") ?? pkgs.GetValueOrDefault(inc);
                        if (ver is not null) pkgRefs[inc] = ver;
                        else if (cpmActive) pkgRefs.TryAdd(inc, "");   // version lives in central props
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
                    case "ProjectCapability":
                        // nanoFramework test projects mark themselves with
                        // <ProjectCapability Include="TestContainer" />; this is a
                        // meaningful item the SDK does not supply, so carry it through.
                        keepItems.Add(el);
                        break;
                    default:
                        review.Add($"Unhandled item <{tag} Include='{inc}'>");
                        break;
                }
            }

        FoldNuspec(projDir, SetProp);

        var xml = Emit(props, pkgRefs, projRefs, keepItems, o, cpmActive);

        var outPath = Path.ChangeExtension(Path.GetFullPath(nfproj), o.Ext);
        var nfFull = Path.GetFullPath(nfproj);
        var replacingNfproj = !string.Equals(outPath, nfFull, StringComparison.OrdinalIgnoreCase);

        var result = new ConvertResult { OutputPath = outPath };
        result.Review.AddRange(review);
        result.Packages.AddRange(pkgRefs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase));

        // Under CPM, each referenced id must have a <PackageVersion> in the nearest
        // Directory.Packages.props. Compute the ids whose version we know (from
        // packages.config / HintPath) that are missing from the central props, and
        // record them; the real run adds them idempotently below. Versionless ids
        // (no resolvable version) cannot seed a PackageVersion and are left as-is.
        var cpmAdditions = new List<KeyValuePair<string, string>>();
        if (cpmActive && cpmPropsPath is not null)
        {
            var existing = LoadPackageVersionIds(cpmPropsPath);
            foreach (var kv in pkgRefs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                if (!string.IsNullOrEmpty(kv.Value) && !existing.Contains(kv.Key))
                    cpmAdditions.Add(kv);
            foreach (var add in cpmAdditions)
                result.AddedPackageVersions.Add(add);
            if (cpmAdditions.Count > 0) result.UpdatedPackagesProps = cpmPropsPath;
        }

        // Compute the set of files that will be (or, in dry-run, would be) removed
        // and the solutions that will be retargeted. This drives the dry-run
        // preview and is identical to what the real run acts on.
        if (replacingNfproj) result.DeletedFiles.Add(nfFull);
        var pc = Path.Combine(projDir, "packages.config");
        if (File.Exists(pc)) result.DeletedFiles.Add(Path.GetFullPath(pc));
        foreach (var ai in ExistingAssemblyInfo(projDir)) result.DeletedFiles.Add(ai);
        // The .sln/.slnx that still reference the .nfproj are what a rewrite would
        // touch. We always populate this preview list (even when SkipSolutionRewrite
        // is set, so the host can render it); the actual rewrite below is gated.
        if (replacingNfproj)
            foreach (var sln in SolutionsReferencing(projDir, nfFull)) result.UpdatedSolutions.Add(sln);

        if (!o.DryRun)
        {
            // Never clobber an existing backup: the original first-run .bak must
            // survive reruns.
            if (!o.NoBackup && !File.Exists(nfproj + ".bak")) File.Copy(nfproj, nfproj + ".bak", overwrite: false);
            File.WriteAllText(outPath, xml, new UTF8Encoding(false));
            // Add any missing <PackageVersion> entries to the central props,
            // idempotently (existing entries are neither duplicated nor reordered).
            if (cpmActive && cpmPropsPath is not null && cpmAdditions.Count > 0)
                AddPackageVersions(cpmPropsPath, cpmAdditions);
            // If we emitted a .csproj alongside, retire the original .nfproj.
            if (replacingNfproj)
            {
                File.Delete(nfproj);
                // Point any .sln/.slnx entries at the new .csproj (idempotent),
                // unless the host is driving solution rewriting itself.
                if (!o.SkipSolutionRewrite) UpdateSolutions(projDir, nfFull);
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

    // The .sln/.slnx files that currently still reference the .nfproj (i.e. those
    // UpdateSolutions would rewrite). Used to preview solution edits in dry-run.
    // Resolves entries through the SolutionFile parser so both classic and XML
    // solutions are considered and only genuine references count.
    private static IEnumerable<string> SolutionsReferencing(string projDir, string nfproj)
    {
        var target = Path.GetFullPath(nfproj);
        foreach (var sln in FindSolutionFiles(projDir))
        {
            SolutionFile parsed;
            try { parsed = SolutionFile.Load(sln); }
            catch { continue; }
            if (parsed.ProjectPaths.Any(p => string.Equals(Path.GetFullPath(p), target, StringComparison.OrdinalIgnoreCase)))
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

    // Walks UP from the project directory looking for the nearest
    // Directory.Packages.props (the file MSBuild itself would pick for CPM). Returns
    // its full path, or null if none exists in the ancestor chain. Pure: read-only.
    private static string? FindDirectoryPackagesProps(string projDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(projDir));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // True when a Directory.Packages.props opts into CPM via
    // <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>.
    // A malformed/unreadable file is treated as "not enabled" (never throws).
    private static bool IsCpmEnabled(string propsPath)
    {
        try
        {
            var root = XElement.Load(propsPath);
            var flag = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "ManagePackageVersionsCentrally");
            return flag is not null
                && bool.TryParse(flag.Value.Trim(), out var on) && on;
        }
        catch { return false; }
    }

    // The set of package ids already declared as <PackageVersion> in a
    // Directory.Packages.props. Used to avoid duplicating entries on add/re-run.
    private static HashSet<string> LoadPackageVersionIds(string propsPath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pv in XElement.Load(propsPath).Descendants()
                         .Where(e => e.Name.LocalName == "PackageVersion"))
            {
                var id = (string?)pv.Attribute("Include");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
        }
        catch { /* unreadable props → treat as having no entries */ }
        return ids;
    }

    // Idempotently inserts <PackageVersion Include="id" Version="ver" /> entries for
    // the given (id, version) pairs into the nearest ItemGroup of a
    // Directory.Packages.props. Existing entries are neither duplicated nor
    // reordered; only genuinely-missing ids are appended. Preserves the file's
    // namespace and overall shape. Caller has already filtered to missing ids.
    private static void AddPackageVersions(string propsPath, IEnumerable<KeyValuePair<string, string>> additions)
    {
        var toAdd = additions.ToList();
        if (toAdd.Count == 0) return;

        var doc = System.Xml.Linq.XDocument.Load(propsPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var root = doc.Root!;
        var ns = root.Name.Namespace;
        var existing = root.Descendants().Where(e => e.Name.LocalName == "PackageVersion")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Reuse the ItemGroup that already holds PackageVersion items; otherwise the
        // first ItemGroup; otherwise create a fresh one.
        var group = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ItemGroup"
                        && e.Elements().Any(c => c.Name.LocalName == "PackageVersion"))
                 ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "ItemGroup");
        if (group is null)
        {
            group = new XElement(ns + "ItemGroup");
            root.Add(group);
        }

        // Match the indentation of an existing PackageVersion so appended entries
        // line up; fall back to a sensible default for a freshly-created group.
        var indent = ElementIndent(group.Elements()
            .FirstOrDefault(c => c.Name.LocalName == "PackageVersion")) ?? "    ";

        // The last PackageVersion to append after (so new entries sit with the rest,
        // before the ItemGroup's closing tag rather than after its trailing newline).
        var anchor = group.Elements().LastOrDefault(c => c.Name.LocalName == "PackageVersion");

        foreach (var kv in toAdd)
        {
            if (existing.Contains(kv.Key)) continue;  // idempotent guard
            var pv = new XElement(ns + "PackageVersion",
                new XAttribute("Include", kv.Key),
                new XAttribute("Version", kv.Value));
            if (anchor is not null)
            {
                anchor.AddAfterSelf(pv);
                anchor.AddAfterSelf(new System.Xml.Linq.XText("\n" + indent));
            }
            else
            {
                group.AddFirst(pv);
                group.AddFirst(new System.Xml.Linq.XText("\n" + indent));
            }
            anchor = pv;
            existing.Add(kv.Key);
        }

        doc.Save(propsPath);
    }

    // The leading-whitespace indentation of an element, read from the immediately
    // preceding whitespace text node. Null if there is no such node (or no element).
    private static string? ElementIndent(XElement? el)
    {
        if (el?.PreviousNode is System.Xml.Linq.XText t)
        {
            var s = t.Value;
            var nl = s.LastIndexOf('\n');
            return nl >= 0 ? s[(nl + 1)..] : s;
        }
        return null;
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

    // Rewrites .sln/.slnx entries that reference the converted .nfproj so they point
    // at the new .csproj (classic .sln also swaps the project-type GUID). Searches
    // walking up from the project directory to the repo root (the dir containing
    // .git), plus any solution in the project's own directory tree. The actual
    // retarget is delegated to SolutionRewriter, which is line/path-scoped and
    // idempotent, so re-running is a no-op.
    private static void UpdateSolutions(string projDir, string nfproj)
    {
        var converted = new[] { Path.GetFullPath(nfproj) };
        foreach (var sln in FindSolutionFiles(projDir))
        {
            SolutionFile parsed;
            try { parsed = SolutionFile.Load(sln); }
            catch { continue; }
            SolutionRewriter.RewriteFile(parsed, converted);
        }
    }

    private static List<string> FindSolutionFiles(string projDir)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSolutionsIn(string d, SearchOption option)
        {
            foreach (var pattern in new[] { "*.sln", "*.slnx" })
                foreach (var sln in Directory.EnumerateFiles(d, pattern, option))
                    found.Add(Path.GetFullPath(sln));
        }

        // Walk up to the repo root (the directory containing .git), collecting
        // solution files at each level.
        var dir = new DirectoryInfo(Path.GetFullPath(projDir));
        while (dir is not null)
        {
            AddSolutionsIn(dir.FullName, SearchOption.TopDirectoryOnly);
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git")))
                break; // reached the repo root
            dir = dir.Parent;
        }

        // Also any solution anywhere in the project's own directory tree.
        AddSolutionsIn(projDir, SearchOption.AllDirectories);

        return found.ToList();
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
        ConversionOptions o,
        bool cpmActive)
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
                // Under CPM the version belongs in the central props, not here — a
                // Version attribute would trip NU1008. Otherwise pin it inline.
                sb.Append(cpmActive
                    ? $"    <PackageReference Include=\"{kv.Key}\" />\n"
                    : $"    <PackageReference Include=\"{kv.Key}\" Version=\"{kv.Value}\" />\n");
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
