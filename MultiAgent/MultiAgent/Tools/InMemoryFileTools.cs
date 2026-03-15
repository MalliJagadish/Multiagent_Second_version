using System.ComponentModel;
using System.Text;

namespace MultiAgent.Tools;

/// <summary>
/// In-memory file tools — replaces FileSystemTools for diskless operation.
/// All files live in PipelineState.Files (Dictionary&lt;string, string&gt;)
/// and get committed to GitHub directly via the Git Data API.
/// </summary>
public static class InMemoryFileTools
{
    [Description("Write a file to the repository (in-memory). Creates the file for later commit to GitHub.")]
    public static string WriteFile(
        Dictionary<string, string> files, string relativePath, string content)
    {
        var cleanPath = relativePath.TrimStart('/').Replace("\\", "/");
        files[cleanPath] = content;
        return $"✅ Written: {cleanPath} ({content.Length} chars) [in-memory, {files.Count} files total]";
    }

    [Description("List all files currently staged in memory.")]
    public static string ListFiles(Dictionary<string, string> files)
    {
        if (files.Count == 0)
            return "No files yet. Use WriteFile to create files.";

        return string.Join("\n", files.Keys.OrderBy(k => k));
    }

    [Description("Read all source code files currently staged in memory.")]
    public static string ReadAllFiles(
        Dictionary<string, string> files, string extensions = ".cs,.ts,.vue,.json")
    {
        var exts = extensions.Split(',').Select(e => e.Trim()).ToArray();
        var sb = new StringBuilder();

        foreach (var (path, content) in files.OrderBy(f => f.Key))
        {
            if (exts.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine($"// ═══ FILE: {path} ═══");
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No matching files found.";
    }
}