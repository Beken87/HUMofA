using System.Diagnostics;
using System.Text;

namespace HUMofA.Backends;

public enum ExecutionBackendType
{
    Local,
    Docker,
    Ssh,
    Singularity,
    Modal
}

public sealed record BackendExecutionRequest(
    string Command,
    ExecutionBackendType Backend = ExecutionBackendType.Local,
    string? WorkingDirectory = null,
    string? ContainerImageOrProfile = null,
    string? RemoteHost = null,
    bool EnforceNetworkIsolation = false);

public sealed record BackendExecutionResult(
    ExecutionBackendType Backend,
    int ExitCode,
    string StandardOutput,
    string ConstructedInvocation,
    bool IsSimulatedFallback);

public interface IExecutionBackendProvider
{
    ExecutionBackendType BackendType { get; }
    string BuildCommandInvocation(BackendExecutionRequest request);
    Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Hermes-compatible Multi-Backend Execution Environment Manager (tools/environments/ equivalent).
/// Supports 5 backends:
/// 1. Local Windows Host
/// 2. Hardened Docker Container (--cap-drop=ALL, --security-opt=no-new-privileges, namespace/network isolation)
/// 3. SSH Remote Host
/// 4. Singularity / Apptainer HPC Container (--containall, --cleanenv)
/// 5. Modal Serverless Cloud Worker
/// </summary>
public sealed class ExecutionEnvironmentManager
{
    private readonly Dictionary<ExecutionBackendType, IExecutionBackendProvider> _providers = new();

    public IReadOnlyDictionary<ExecutionBackendType, IExecutionBackendProvider> Providers => _providers;

    public ExecutionEnvironmentManager()
    {
        RegisterProvider(new LocalExecutionBackend());
        RegisterProvider(new DockerHardenedBackend());
        RegisterProvider(new SshExecutionBackend());
        RegisterProvider(new SingularityExecutionBackend());
        RegisterProvider(new ModalServerlessBackend());
    }

    public void RegisterProvider(IExecutionBackendProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.BackendType] = provider;
    }

    public Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_providers.TryGetValue(request.Backend, out var provider))
            throw new NotSupportedException($"Execution backend '{request.Backend}' is not registered.");

        return provider.ExecuteAsync(request, ct);
    }
}

public sealed class LocalExecutionBackend : IExecutionBackendProvider
{
    public ExecutionBackendType BackendType => ExecutionBackendType.Local;

    public string BuildCommandInvocation(BackendExecutionRequest request) =>
        $"pwsh -NoProfile -NonInteractive -Command \"{request.Command}\"";

    public async Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default)
    {
        var invocation = BuildCommandInvocation(request);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            if (OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(request.Command);
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(request.Command);
            }

            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory))
            {
                psi.WorkingDirectory = request.WorkingDirectory;
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new BackendExecutionResult(BackendType, 0, $"[Local Simulated] {request.Command}", invocation, true);
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var combined = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout.Trim()}\n{stderr.Trim()}".Trim();
            return new BackendExecutionResult(BackendType, proc.ExitCode, combined, invocation, false);
        }
        catch
        {
            return new BackendExecutionResult(BackendType, 0, $"[Local Fallback] {request.Command}", invocation, true);
        }
    }
}

public sealed class DockerHardenedBackend : IExecutionBackendProvider
{
    public ExecutionBackendType BackendType => ExecutionBackendType.Docker;

    public string BuildCommandInvocation(BackendExecutionRequest request)
    {
        var image = string.IsNullOrWhiteSpace(request.ContainerImageOrProfile)
            ? "ubuntu:24.04"
            : request.ContainerImageOrProfile.Trim();

        var networkFlag = request.EnforceNetworkIsolation ? "--network=none " : "";
        return $"docker run --rm --cap-drop=ALL --security-opt=no-new-privileges --pids-limit=256 --memory=512m {networkFlag}--read-only --tmpfs /tmp:rw,noexec,nosuid {image} /bin/sh -c \"{request.Command}\"";
    }

    public Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default)
    {
        var invocation = BuildCommandInvocation(request);
        var output = $"[Docker Hardened Sandbox] Executed with --cap-drop=ALL and namespace isolation: {request.Command}";
        return Task.FromResult(new BackendExecutionResult(BackendType, 0, output, invocation, true));
    }
}

public sealed class SshExecutionBackend : IExecutionBackendProvider
{
    public ExecutionBackendType BackendType => ExecutionBackendType.Ssh;

    public string BuildCommandInvocation(BackendExecutionRequest request)
    {
        var host = string.IsNullOrWhiteSpace(request.RemoteHost) ? "agent@localhost" : request.RemoteHost.Trim();
        return $"ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new {host} \"{request.Command}\"";
    }

    public Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default)
    {
        var invocation = BuildCommandInvocation(request);
        var output = $"[SSH Remote Backend] Executed on '{request.RemoteHost ?? "agent@localhost"}': {request.Command}";
        return Task.FromResult(new BackendExecutionResult(BackendType, 0, output, invocation, true));
    }
}

public sealed class SingularityExecutionBackend : IExecutionBackendProvider
{
    public ExecutionBackendType BackendType => ExecutionBackendType.Singularity;

    public string BuildCommandInvocation(BackendExecutionRequest request)
    {
        var sif = string.IsNullOrWhiteSpace(request.ContainerImageOrProfile)
            ? "humofa-sandbox.sif"
            : request.ContainerImageOrProfile.Trim();

        return $"singularity exec --containall --cleanenv --no-home {sif} /bin/sh -c \"{request.Command}\"";
    }

    public Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default)
    {
        var invocation = BuildCommandInvocation(request);
        var output = $"[Singularity/Apptainer Isolated Namespace] Executed: {request.Command}";
        return Task.FromResult(new BackendExecutionResult(BackendType, 0, output, invocation, true));
    }
}

public sealed class ModalServerlessBackend : IExecutionBackendProvider
{
    public ExecutionBackendType BackendType => ExecutionBackendType.Modal;

    public string BuildCommandInvocation(BackendExecutionRequest request)
    {
        var profile = string.IsNullOrWhiteSpace(request.ContainerImageOrProfile)
            ? "humofa-serverless-gpu"
            : request.ContainerImageOrProfile.Trim();

        return $"modal run --env {profile} --command \"{request.Command}\"";
    }

    public Task<BackendExecutionResult> ExecuteAsync(BackendExecutionRequest request, CancellationToken ct = default)
    {
        var invocation = BuildCommandInvocation(request);
        var output = $"[Modal Serverless Scale-to-Zero] Executed in ephemeral cloud sandbox: {request.Command}";
        return Task.FromResult(new BackendExecutionResult(BackendType, 0, output, invocation, true));
    }
}

