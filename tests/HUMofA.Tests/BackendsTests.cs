using FluentAssertions;
using HUMofA.Backends;
using Xunit;

namespace HUMofA.Tests;

public class BackendsTests
{
    [Fact]
    public async Task ExecutionEnvironmentManager_SupportsAllFiveBackends_WithContainerHardening()
    {
        var manager = new ExecutionEnvironmentManager();
        manager.Providers.Should().HaveCount(5);

        // 1. Local Windows
        var localRes = await manager.ExecuteAsync(new BackendExecutionRequest("echo humofa-local", ExecutionBackendType.Local));
        localRes.ExitCode.Should().Be(0);
        localRes.StandardOutput.Should().Contain("humofa-local");

        // 2. Hardened Docker (--cap-drop=ALL and --security-opt=no-new-privileges)
        var dockerRes = await manager.ExecuteAsync(new BackendExecutionRequest(
            "uname -a",
            ExecutionBackendType.Docker,
            ContainerImageOrProfile: "python:3.14-slim",
            EnforceNetworkIsolation: true));
        dockerRes.ExitCode.Should().Be(0);
        dockerRes.ConstructedInvocation.Should().Contain("--cap-drop=ALL");
        dockerRes.ConstructedInvocation.Should().Contain("--security-opt=no-new-privileges");
        dockerRes.ConstructedInvocation.Should().Contain("--network=none");
        dockerRes.ConstructedInvocation.Should().Contain("--read-only");

        // 3. SSH Remote Host
        var sshRes = await manager.ExecuteAsync(new BackendExecutionRequest(
            "uptime",
            ExecutionBackendType.Ssh,
            RemoteHost: "admin@10.0.0.5"));
        sshRes.ExitCode.Should().Be(0);
        sshRes.ConstructedInvocation.Should().Contain("BatchMode=yes");
        sshRes.ConstructedInvocation.Should().Contain("admin@10.0.0.5");

        // 4. Singularity / Apptainer HPC
        var singRes = await manager.ExecuteAsync(new BackendExecutionRequest(
            "python3 --version",
            ExecutionBackendType.Singularity,
            ContainerImageOrProfile: "humofa.sif"));
        singRes.ExitCode.Should().Be(0);
        singRes.ConstructedInvocation.Should().Contain("--containall");
        singRes.ConstructedInvocation.Should().Contain("--cleanenv");

        // 5. Modal Serverless
        var modalRes = await manager.ExecuteAsync(new BackendExecutionRequest(
            "train.py::main",
            ExecutionBackendType.Modal));
        modalRes.ExitCode.Should().Be(0);
        modalRes.ConstructedInvocation.Should().StartWith("modal run");
    }
}
