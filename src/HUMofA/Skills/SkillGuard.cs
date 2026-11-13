using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HUMofA.Skills;

public sealed record SecurityScanResult(bool IsSafe, IReadOnlyList<string> Violations);

public interface ISkillGuard
{
    Task<SecurityScanResult> ScanPythonCodeAsync(string code, CancellationToken ct = default);
    Task<SecurityScanResult> ScanSkillFileAsync(string skillPath, CancellationToken ct = default);
    Task<SecurityScanResult> ScanSkillMarkdownContentAsync(string markdownContent, CancellationToken ct = default);
}

/// <summary>
/// AST and lexical security scanner (Hermes skills_ast_audit.py / skills_guard.py equivalent).
/// Inspects Python code and SKILL.md code blocks for forbidden execution primitives, shell injection,
/// credential exfiltration, and destructive filesystem commands.
/// </summary>
public sealed class SkillGuard : ISkillGuard
{
    private static readonly (Regex Pattern, string Reason)[] StaticRules =
    [
        (new Regex(@"\bos\s*\.\s*(system|popen|spawn[lv]?[pe]?|exec[lv]?[pe]?)\s*\(", RegexOptions.Compiled),
            "Forbidden OS command execution call (os.system/popen/spawn/exec)"),
        (new Regex(@"\bsubprocess\s*\.\s*(Popen|run|call|check_output|check_call)\s*\(", RegexOptions.Compiled),
            "Forbidden subprocess execution primitive"),
        (new Regex(@"(?<![\w\.])(eval|exec|compile)\s*\(", RegexOptions.Compiled),
            "Forbidden dynamic code evaluation (eval/exec/compile)"),
        (new Regex(@"__import__\s*\(", RegexOptions.Compiled),
            "Forbidden dynamic module import (__import__)"),
        (new Regex(@"\b(pickle|marshal|shelve)\s*\.\s*(loads|load)\s*\(", RegexOptions.Compiled),
            "Unsafe deserialization primitive (pickle/marshal/shelve)"),
        (new Regex(@"\bctypes\b", RegexOptions.Compiled),
            "Forbidden native memory/DLL access via ctypes"),
        (new Regex(@"\bshutil\s*\.\s*rmtree\s*\(", RegexOptions.Compiled),
            "Destructive recursive directory deletion (shutil.rmtree)"),
        (new Regex(@"\bsocket\s*\.\s*socket\s*\(", RegexOptions.Compiled),
            "Raw socket creation detected (potential reverse shell or exfiltration)"),
        (new Regex(@"\b(Invoke-Expression|iex)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "Forbidden PowerShell Invoke-Expression / iex primitive"),
        (new Regex(@"\brm\s+-rf\s+(/|\*|~)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "Destructive shell deletion command (rm -rf)")
    ];

    private const string PythonAstAuditScript = """
        import ast, json, sys

        FORBIDDEN_CALLS = {"eval", "exec", "compile", "__import__"}
        FORBIDDEN_ATTRS = {
            "os": {"system", "popen", "spawnl", "spawnle", "spawnlp", "spawnlpe", "spawnv", "spawnve", "spawnvp", "spawnvpe", "execl", "execle", "execlp", "execlpe", "execv", "execve", "execvp", "execvpe", "remove", "unlink", "rmdir"},
            "subprocess": {"Popen", "run", "call", "check_call", "check_output"},
            "pickle": {"load", "loads"},
            "marshal": {"load", "loads"},
            "shutil": {"rmtree"},
        }
        FORBIDDEN_IMPORTS = {"ctypes", "pty"}

        code = sys.stdin.read()
        violations = []
        try:
            tree = ast.parse(code)
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    for alias in node.names:
                        root = alias.name.split(".")[0]
                        if root in FORBIDDEN_IMPORTS:
                            violations.append(f"Line {node.lineno}: Forbidden import '{alias.name}'")
                elif isinstance(node, ast.ImportFrom):
                    mod = (node.module or "").split(".")[0]
                    if mod in FORBIDDEN_IMPORTS:
                        violations.append(f"Line {node.lineno}: Forbidden from-import '{node.module}'")
                    if mod in FORBIDDEN_ATTRS:
                        for alias in node.names:
                            if alias.name in FORBIDDEN_ATTRS[mod] or alias.name == "*":
                                violations.append(f"Line {node.lineno}: Forbidden import '{mod}.{alias.name}'")
                elif isinstance(node, ast.Call):
                    if isinstance(node.func, ast.Name) and node.func.id in FORBIDDEN_CALLS:
                        violations.append(f"Line {node.lineno}: Forbidden builtin call '{node.func.id}()'")
                    elif isinstance(node.func, ast.Attribute) and isinstance(node.func.value, ast.Name):
                        mod = node.func.value.id
                        attr = node.func.attr
                        if mod in FORBIDDEN_ATTRS and attr in FORBIDDEN_ATTRS[mod]:
                            violations.append(f"Line {node.lineno}: Forbidden call '{mod}.{attr}()'")
        except SyntaxError as exc:
            violations.append(f"SyntaxError at line {exc.lineno}: {exc.msg}")

        print(json.dumps({"violations": violations}))
        """;

    private readonly bool _enablePythonSubprocessAst;

    public SkillGuard(bool enablePythonSubprocessAst = true)
    {
        _enablePythonSubprocessAst = enablePythonSubprocessAst;
    }

    public async Task<SecurityScanResult> ScanPythonCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new SecurityScanResult(true, Array.Empty<string>());

        var violations = new List<string>();

        // 1. Deterministic C# static & lexical AST rules (instant, works even without Python)
        foreach (var (pattern, reason) in StaticRules)
        {
            if (pattern.IsMatch(code))
            {
                violations.Add(reason);
            }
        }

        // 2. Python 3.14 native ast.parse audit
        if (_enablePythonSubprocessAst)
        {
            var pyViolations = await TryRunPythonAstAuditAsync(code, ct).ConfigureAwait(false);
            foreach (var v in pyViolations)
            {
                if (!violations.Contains(v, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add(v);
                }
            }
        }

        return new SecurityScanResult(violations.Count == 0, violations);
    }

    public async Task<SecurityScanResult> ScanSkillFileAsync(string skillPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillPath);
        if (!File.Exists(skillPath))
            return new SecurityScanResult(false, [$"Skill file not found: {skillPath}"]);

        var content = await File.ReadAllTextAsync(skillPath, Encoding.UTF8, ct).ConfigureAwait(false);
        return await ScanSkillMarkdownContentAsync(content, ct).ConfigureAwait(false);
    }

    public async Task<SecurityScanResult> ScanSkillMarkdownContentAsync(string markdownContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
            return new SecurityScanResult(true, Array.Empty<string>());

        var violations = new List<string>();

        // Check full markdown against static rules
        foreach (var (pattern, reason) in StaticRules)
        {
            if (pattern.IsMatch(markdownContent))
            {
                violations.Add(reason);
            }
        }

        // Extract fenced python code blocks and run full Python AST audit
        var codeBlockMatches = Regex.Matches(
            markdownContent,
            @"```(?:python|py)\s*\r?\n(.*?)```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in codeBlockMatches)
        {
            var pyCode = match.Groups[1].Value;
            var blockScan = await ScanPythonCodeAsync(pyCode, ct).ConfigureAwait(false);
            if (!blockScan.IsSafe)
            {
                foreach (var v in blockScan.Violations)
                {
                    if (!violations.Contains(v, StringComparer.OrdinalIgnoreCase))
                        violations.Add(v);
                }
            }
        }

        return new SecurityScanResult(violations.Count == 0, violations);
    }

    private static async Task<IReadOnlyList<string>> TryRunPythonAstAuditAsync(string code, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(PythonAstAuditScript);

            using var proc = Process.Start(psi);
            if (proc is null)
                return Array.Empty<string>();

            await proc.StandardInput.WriteAsync(code.AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stdout))
                return Array.Empty<string>();

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("violations", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
                return list;
            }
        }
        catch
        {
            // Fallback gracefully to static rules if Python process is unavailable or timed out
        }

        return Array.Empty<string>();
    }
}
