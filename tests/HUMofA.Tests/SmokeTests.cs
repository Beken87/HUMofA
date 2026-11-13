using FluentAssertions;
using Xunit;

namespace HUMofA.Tests;

public class SmokeTests
{
    [Fact]
    public void Program_Version_Returns100()
    {
        // Assert that the static version property exposes "1.0.0"
        Program.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task AppCore_InitializesSuccessfully_WithCorrectVersion()
    {
        // Arrange
        var app = new AppCore();

        // Act
        await app.InitializeAsync(isHeadless: false);

        // Assert
        app.IsInitialized.Should().BeTrue();
        app.IsHeadless.Should().BeFalse();
        app.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task AppCore_HeadlessMode_SetsHeadlessFlag()
    {
        // Arrange
        var app = new AppCore();

        // Act
        await app.InitializeAsync(isHeadless: true);

        // Assert
        app.IsInitialized.Should().BeTrue();
        app.IsHeadless.Should().BeTrue();
        app.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Program_Main_HeadlessArgument_ReturnsExitCodeZero()
    {
        // Arrange
        string[] args = ["--headless"];

        // Act
        int exitCode = await Program.Main(args);

        // Assert
        exitCode.Should().Be(0);
    }
}
