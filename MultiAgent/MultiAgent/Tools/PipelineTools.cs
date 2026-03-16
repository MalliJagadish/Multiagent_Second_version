using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MultiAgent.Tools;

/// <summary>
/// MCP Tools — the "hands" of each agent.
///
/// HOW THIS WORKS:
/// 1. Agent LLM decides it needs to read/write a file
/// 2. LLM emits a tool_use call: { "name": "WriteFile", "args": {...} }
/// 3. MAF sees it, finds the matching AIFunctionFactory.Create() registration
/// 4. Calls this C# method, gets result
/// 5. Injects result back into agent's context as a tool_result
/// 6. Agent continues reasoning with the new info
///
/// Protocol: MCP (Model Context Protocol) — JSON-RPC 2.0 in-process calls
/// </summary>
public static class FileSystemTools
{
    [Description("Read all source code files from the repository.")]
    public static string ReadRepoFiles(string repoPath, string extensions = ".cs,.ts,.vue,.json")
    {
        var sb = new StringBuilder();
        var exts = extensions.Split(',').Select(e => e.Trim()).ToArray();

        foreach (var ext in exts)
        {
            var files = Directory.GetFiles(repoPath, $"*{ext}", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                         && !f.Contains("\\.git\\") && !f.Contains("\\node_modules\\"));

            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(repoPath, file);
                sb.AppendLine($"// ═══ FILE: {rel} ═══");
                sb.AppendLine(File.ReadAllText(file));
                sb.AppendLine();
            }
        }

        return sb.Length > 0 ? sb.ToString() : "No files found.";
    }

    [Description("Write a file to the repository. Creates directories if needed.")]
    public static async Task<string> WriteFile(string repoPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        return $"✅ Written: {relativePath} ({content.Length} chars)";
    }

    [Description("List all files currently in the repository.")]
    public static string ListFiles(string repoPath)
    {
        var files = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                     && !f.Contains("\\.git\\"))
            .Select(f => Path.GetRelativePath(repoPath, f));

        return string.Join("\n", files);
    }
}

public static class TerminalTools
{
    [Description("Run a terminal command and return its output.")]
    public static async Task<string> RunCommand(
        string command, string workingDirectory, int timeoutSeconds = 120)
    {
        var parts = command.Split(' ', 2);
        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            Arguments = parts.Length > 1 ? parts[1] : "",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var process = Process.Start(psi)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAny(
                process.WaitForExitAsync(),
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

            var output = await outputTask;
            var error = await errorTask;
            var exitCode = process.HasExited ? process.ExitCode : -1;

            return $"ExitCode: {exitCode}\n{output}\n{error}".Trim();
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }
}

public static class GitTools
{
    [Description("Create a new git branch and switch to it.")]
    public static async Task<string> CreateBranch(string repoPath, string branchName)
        => await TerminalTools.RunCommand($"git checkout -b {branchName}", repoPath);

    [Description("Stage all files and create a git commit.")]
    public static async Task<string> CommitAll(string repoPath, string message)
    {
        await TerminalTools.RunCommand("git add .", repoPath);
        return await TerminalTools.RunCommand($"git commit -m \"{message}\"", repoPath);
    }
}

public static class SecurityScanTools
{
    [Description("Run Semgrep static security analysis on the repository.")]
    public static async Task<string> RunSemgrep(string repoPath)
    {
        var result = await TerminalTools.RunCommand(
            "semgrep --config=auto --json .", repoPath, timeoutSeconds: 60);

        if (result.Contains("command not found") || result.Contains("is not recognized"))
            return "⚠️ Semgrep not installed. Run: pip install semgrep\nSkipping static scan.";

        return $"Semgrep Results:\n{result}";
    }

    [Description("Scan code for hardcoded secrets, API keys, passwords.")]
    public static string ScanForSecrets(string code)
    {
        var patterns = new[]
        {
            ("API Key",        @"[Aa][Pp][Ii][-_]?[Kk][Ee][Yy]\s*=\s*[""][^""]+[""]"),
            ("Password",       @"[Pp]assword\s*=\s*[""][^""]+[""]"),
            ("Connection str", @"[Ss]erver\s*=\s*.+[Pp]assword\s*="),
            ("JWT Secret",     @"[Jj][Ww][Tt].*[Ss]ecret\s*=\s*[""][^""]+[""]"),
        };

        var findings = new StringBuilder();
        foreach (var (name, pattern) in patterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(code, pattern);
            if (matches.Count > 0)
                findings.AppendLine($"⚠️ {name}: {matches.Count} match(es)");
        }

        return findings.Length > 0
            ? $"SECRET SCAN FINDINGS:\n{findings}"
            : "✅ No hardcoded secrets detected.";
    }
}