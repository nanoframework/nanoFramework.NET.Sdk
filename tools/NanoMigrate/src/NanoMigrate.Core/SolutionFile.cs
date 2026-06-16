using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NanoFramework.Migrate.Core;

/// <summary>The on-disk format of a Visual Studio solution.</summary>
public enum SolutionFormat
{
    /// <summary>Classic line-based <c>.sln</c> (each project has a type GUID).</summary>
    Classic,

    /// <summary>XML <c>.slnx</c> (path-based; no per-project type GUID).</summary>
    Xml,
}

/// <summary>
/// Parsed model of a Visual Studio solution — both the classic <c>.sln</c> line
/// format and the newer XML <c>.slnx</c> format. Exposes the referenced project
/// paths (absolute) and the format. Pure: parsing reads the file, nothing else.
/// </summary>
public sealed class SolutionFile
{
    // The project-declaration line of a classic .sln:
    //   Project("{type-guid}") = "Name", "rel\path.ext", "{project-guid}"
    // We only need the *path* (the second quoted value). The capture is
    // separator- and extension-agnostic; callers filter on .nfproj/.csproj.
    private static readonly Regex ClassicProjectLine = new(
        "^\\s*Project\\(\"\\{[^}]*\\}\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Absolute path to the solution file.</summary>
    public string Path { get; }

    /// <summary>Whether this is a classic <c>.sln</c> or an XML <c>.slnx</c>.</summary>
    public SolutionFormat Format { get; }

    /// <summary>
    /// Every project the solution references, as an absolute path. Solution folders
    /// (which in a classic <c>.sln</c> are "projects" whose path equals the folder
    /// name, with no separator and no project extension) are excluded.
    /// </summary>
    public IReadOnlyList<string> ProjectPaths { get; }

    private SolutionFile(string path, SolutionFormat format, IReadOnlyList<string> projectPaths)
    {
        Path = path;
        Format = format;
        ProjectPaths = projectPaths;
    }

    /// <summary>True if the file extension denotes a solution (<c>.sln</c> or <c>.slnx</c>).</summary>
    public static bool IsSolutionPath(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads and parses the solution at <paramref name="solutionPath"/>.</summary>
    public static SolutionFile Load(string solutionPath)
    {
        var full = System.IO.Path.GetFullPath(solutionPath);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        var text = File.ReadAllText(full);

        return full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? new SolutionFile(full, SolutionFormat.Xml, ParseSlnx(text, dir))
            : new SolutionFile(full, SolutionFormat.Classic, ParseClassic(text, dir));
    }

    /// <summary>
    /// The <c>.nfproj</c> projects referenced by the solution (absolute paths).
    /// </summary>
    public IReadOnlyList<string> NanoProjects() =>
        ProjectPaths.Where(p => p.EndsWith(".nfproj", StringComparison.OrdinalIgnoreCase)).ToList();

    // Classic .sln: scan project-declaration lines, resolve each quoted relative
    // path against the solution directory. Skip solution folders (the path has no
    // directory separator AND no recognised project extension — e.g. "src").
    private static List<string> ParseClassic(string text, string slnDir)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var m = ClassicProjectLine.Match(raw);
            if (!m.Success) continue;
            var rel = m.Groups["path"].Value.Trim();
            if (IsSolutionFolderEntry(rel)) continue;
            result.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(slnDir, rel)));
        }
        return result;
    }

    // A classic .sln solution-folder entry uses the folder name as its "path" — no
    // separators and no project file extension. Real project entries always carry
    // a project extension (the path ends with .*proj).
    private static bool IsSolutionFolderEntry(string rel)
    {
        var hasSeparator = rel.Contains('\\') || rel.Contains('/');
        var looksLikeProject = rel.EndsWith("proj", StringComparison.OrdinalIgnoreCase);
        return !hasSeparator && !looksLikeProject;
    }

    // .slnx: an XML document of nested <Folder>/<Project Path="..."> elements.
    // We collect every <Project Path="..."> regardless of nesting depth.
    private static List<string> ParseSlnx(string text, string slnDir)
    {
        var result = new List<string>();
        var root = XElement.Parse(text);
        foreach (var proj in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Project"))
        {
            var rel = (string?)proj.Attribute("Path");
            if (string.IsNullOrWhiteSpace(rel)) continue;
            result.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(slnDir, rel.Trim())));
        }
        return result;
    }
}
