using System.Text;

namespace NanoFramework.Migrate.Core;

/// <summary>
/// Retargets a solution's converted projects from <c>.nfproj</c> to <c>.csproj</c>.
/// Handles both the classic line-based <c>.sln</c> (path + project-type GUID) and
/// the XML <c>.slnx</c> (path only). Pure transform: <see cref="Rewrite"/> works on
/// strings; <see cref="RewriteFile"/> wraps it with file I/O.
///
/// Idempotent / reentrant: only entries still pointing at a <c>.nfproj</c> that is
/// in the converted set are touched, so a solution already pointing at the
/// <c>.csproj</c> is left byte-for-byte unchanged.
/// </summary>
public static class SolutionRewriter
{
    // Project-type GUIDs: legacy nanoFramework flavor -> SDK-style C#.
    public const string NfprojTypeGuid = "{11A8DD76-328B-46DF-9F39-F559912D0360}";
    public const string CsprojTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";

    /// <summary>
    /// Returns the rewritten text of <paramref name="solutionText"/>, retargeting any
    /// entry whose referenced <c>.nfproj</c> (resolved against <paramref name="solutionDir"/>)
    /// is in <paramref name="convertedNfprojPaths"/> (absolute paths). The returned
    /// string equals the input when nothing matched (no-op).
    /// </summary>
    public static string Rewrite(
        string solutionText,
        string solutionDir,
        SolutionFormat format,
        IEnumerable<string> convertedNfprojPaths)
    {
        var converted = new HashSet<string>(
            convertedNfprojPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        if (converted.Count == 0) return solutionText;

        return format == SolutionFormat.Xml
            ? RewriteSlnx(solutionText, solutionDir, converted)
            : RewriteClassic(solutionText, solutionDir, converted);
    }

    /// <summary>
    /// Rewrites the solution file in place when at least one entry was retargeted.
    /// Returns true if the file changed. A no-op leaves the file untouched (no write).
    /// </summary>
    public static bool RewriteFile(SolutionFile solution, IEnumerable<string> convertedNfprojPaths)
    {
        string text;
        try { text = File.ReadAllText(solution.Path); }
        catch { return false; }

        var dir = Path.GetDirectoryName(solution.Path)!;
        var updated = Rewrite(text, dir, solution.Format, convertedNfprojPaths);
        if (string.Equals(updated, text, StringComparison.Ordinal)) return false;

        File.WriteAllText(solution.Path, updated, new UTF8Encoding(false));
        return true;
    }

    // Classic .sln: line-scoped path + GUID swap. We rewrite line-by-line so the
    // GUID swap stays scoped to the project's own declaration line — a blanket
    // GUID replace would wrongly flip other, still-unconverted nanoFramework
    // projects in a shared solution.
    private static string RewriteClassic(string text, string slnDir, HashSet<string> converted)
    {
        var lines = text.Split('\n');
        var changed = false;
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (!line.TrimStart().StartsWith("Project(", StringComparison.OrdinalIgnoreCase)) continue;

            // The .sln path is relative to the .sln dir and wrapped in quotes. Match
            // the *quoted* path so we rewrite the specific entry rather than blanket-
            // replacing a substring that might collide with another project's name.
            var matched = false;
            foreach (var nf in converted)
            {
                var rel = Path.GetRelativePath(slnDir, nf);
                var nfName = Path.GetFileName(nf);
                var csName = Path.ChangeExtension(nfName, ".csproj");

                foreach (var candidate in QuotedCandidates(rel, nfName))
                {
                    if (!line.Contains(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    var replacement = "\"" + candidate[1..^(nfName.Length + 1)] + csName + "\"";
                    line = ReplaceIgnoreCase(line, candidate, replacement);
                    matched = true;
                }
            }
            if (!matched) continue;

            // Swap the legacy project-type GUID for the SDK-style one, scoped to the
            // line we just retargeted.
            if (line.Contains(NfprojTypeGuid, StringComparison.OrdinalIgnoreCase))
                line = ReplaceIgnoreCase(line, NfprojTypeGuid, CsprojTypeGuid);

            lines[li] = line;
            changed = true;
        }
        return changed ? string.Join('\n', lines) : text;
    }

    // The quoted path forms a .sln might use for one project: relative path in
    // OS-native and both separator styles, plus the bare filename fallback. Each is
    // returned wrapped in the surrounding double quotes.
    private static IEnumerable<string> QuotedCandidates(string rel, string nfName) =>
        new[]
        {
            "\"" + rel + "\"",
            "\"" + rel.Replace('/', '\\') + "\"",
            "\"" + rel.Replace('\\', '/') + "\"",
            "\"" + nfName + "\"",
        }.Distinct(StringComparer.OrdinalIgnoreCase);

    // .slnx: path-based, no per-project GUID. Rewrite the Path attribute value of
    // every <Project Path="...nfproj"> whose resolved path is in the converted set.
    // We edit the raw text (rather than re-serialising the XML) so formatting,
    // attribute order, and comments are preserved.
    private static string RewriteSlnx(string text, string slnDir, HashSet<string> converted)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        var changed = false;

        while (i < text.Length)
        {
            // Find the next Path="..." (any attribute literally named Path).
            var idx = IndexOfPathAttr(text, i);
            if (idx < 0) { sb.Append(text, i, text.Length - i); break; }

            // idx points at "Path"; locate the opening quote after '='.
            var eq = text.IndexOf('=', idx);
            var openQuoteRel = eq < 0 ? -1 : text.IndexOf('"', eq);
            if (openQuoteRel < 0) { sb.Append(text, i, idx + 4 - i); i = idx + 4; continue; }
            var closeQuote = text.IndexOf('"', openQuoteRel + 1);
            if (closeQuote < 0) { sb.Append(text, i, idx + 4 - i); i = idx + 4; continue; }

            var rel = text.Substring(openQuoteRel + 1, closeQuote - openQuoteRel - 1);
            // Append everything up to and including the opening quote unchanged.
            sb.Append(text, i, openQuoteRel + 1 - i);

            if (rel.EndsWith(".nfproj", StringComparison.OrdinalIgnoreCase))
            {
                var abs = Path.GetFullPath(Path.Combine(slnDir, rel.Trim()));
                if (converted.Contains(abs))
                {
                    rel = rel[..^".nfproj".Length] + ".csproj";
                    changed = true;
                }
            }
            sb.Append(rel);
            sb.Append('"');
            i = closeQuote + 1;
        }

        return changed ? sb.ToString() : text;
    }

    // Finds the next occurrence of a literal Path attribute name (case-insensitive,
    // word-bounded so it doesn't match e.g. "HintPath"). Returns the index of the
    // 'P', or -1.
    private static int IndexOfPathAttr(string text, int start)
    {
        int from = start;
        while (true)
        {
            var idx = text.IndexOf("Path", from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
            var prev = idx == 0 ? ' ' : text[idx - 1];
            // Reject when preceded by a name char (e.g. HintPath, FooPath).
            if (!(char.IsLetterOrDigit(prev) || prev == '_')) return idx;
            from = idx + 4;
        }
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
}
