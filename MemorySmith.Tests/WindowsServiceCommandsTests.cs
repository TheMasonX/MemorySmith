using MemorySmith.App.Services;

namespace MemorySmith.Tests;

[TestFixture]
public class WindowsServiceCommandsTests
{
    [Test]
    public void Parse_WithInstallFlag_ReturnsInstallCommandWithDefaults()
    {
        var command = WindowsServiceCommands.Parse(["--install-service"]);

        Assert.Multiple(() =>
        {
            Assert.That(command, Is.Not.Null);
            Assert.That(command!.Kind, Is.EqualTo(WindowsServiceCommandKind.Install));
            Assert.That(command.ServiceName, Is.EqualTo(WindowsServiceCommands.DefaultServiceName));
            Assert.That(command.DisplayName, Is.EqualTo(WindowsServiceCommands.DefaultServiceName));
            Assert.That(command.StartType, Is.EqualTo("auto"));
            Assert.That(command.Port, Is.Null);
            Assert.That(command.MemoryDirectory, Is.Null);
            Assert.That(command.RuntimeArguments, Is.Empty);
        });

        Assert.That(WindowsServiceCommands.BuildRuntimeArguments(command!), Is.EqualTo(new[] { "--urls", "http://localhost:5089" }));
    }

    [Test]
    public void Parse_WithPlainUninstallFlagAndName_ReturnsUninstallCommand()
    {
        var command = WindowsServiceCommands.Parse(["uninstall", "--service-name", "MemorySmith.Dev"]);

        Assert.Multiple(() =>
        {
            Assert.That(command, Is.Not.Null);
            Assert.That(command!.Kind, Is.EqualTo(WindowsServiceCommandKind.Uninstall));
            Assert.That(command.ServiceName, Is.EqualTo("MemorySmith.Dev"));
        });
    }

    [Test]
    public void Parse_WithInstallOptionsAndRuntimeArguments_PreservesRuntimeArgumentsOnly()
    {
        var command = WindowsServiceCommands.Parse([
            "--install-service",
            "--service-name=MemorySmith.Dev",
            "--service-display-name", "MemorySmith Dev",
            "--service-description", "Local wiki service",
            "--service-start-type", "demand",
            "--",
            "--urls", "http://localhost:5089"]);

        Assert.Multiple(() =>
        {
            Assert.That(command, Is.Not.Null);
            Assert.That(command!.ServiceName, Is.EqualTo("MemorySmith.Dev"));
            Assert.That(command.DisplayName, Is.EqualTo("MemorySmith Dev"));
            Assert.That(command.Description, Is.EqualTo("Local wiki service"));
            Assert.That(command.StartType, Is.EqualTo("demand"));
            Assert.That(command.RuntimeArguments, Is.EqualTo(new[] { "--urls", "http://localhost:5089" }));
            Assert.That(WindowsServiceCommands.BuildRuntimeArguments(command), Is.EqualTo(command.RuntimeArguments));
        });
    }

    [Test]
    public void Parse_WithMemoryDirectoryAndPort_BuildsRuntimeConfiguration()
    {
        var memoryDirectory = Path.Combine(Path.GetTempPath(), "Memory Smith Data", "Memories");

        var command = WindowsServiceCommands.Parse([
            "install",
            "--memory-directory", memoryDirectory,
            "--port", "5090"]);

        var runtimeArguments = WindowsServiceCommands.BuildRuntimeArguments(command!).ToList();
        var normalizedMemoryDirectory = Path.GetFullPath(memoryDirectory);
        var dataRoot = Directory.GetParent(normalizedMemoryDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!.FullName;

        Assert.Multiple(() =>
        {
            Assert.That(command, Is.Not.Null);
            Assert.That(command!.Kind, Is.EqualTo(WindowsServiceCommandKind.Install));
            Assert.That(command.Port, Is.EqualTo(5090));
            Assert.That(command.MemoryDirectory, Is.EqualTo(normalizedMemoryDirectory));
            Assert.That(runtimeArguments, Is.EqualTo(new[]
            {
                "--urls", "http://localhost:5090",
                "--MemorySmith:DataPath", normalizedMemoryDirectory,
                "--MemorySmith:PagesPath", Path.Combine(dataRoot, "Pages"),
                "--MemorySmith:EventLogPath", Path.Combine(dataRoot, "Events", "audit.log"),
                "--MemorySmith:VarsPath", Path.Combine(dataRoot, "vars.json")
            }));
        });
    }

    [Test]
    public void Parse_WithHelpFlag_ReturnsHelpCommand()
    {
        var command = WindowsServiceCommands.Parse(["--help"]);

        Assert.Multiple(() =>
        {
            Assert.That(command, Is.Not.Null);
            Assert.That(command!.Kind, Is.EqualTo(WindowsServiceCommandKind.Help));
            Assert.That(WindowsServiceCommands.GetHelpText(), Does.Contain("--memory-directory"));
            Assert.That(WindowsServiceCommands.GetHelpText(), Does.Contain("--port"));
        });
    }

    [Test]
    public void Parse_WithInvalidPort_Throws()
    {
        Assert.Throws<ArgumentException>(() => WindowsServiceCommands.Parse(["install", "--port", "70000"]));
    }

    [Test]
    public void Parse_WithInvalidStartType_Throws()
    {
        Assert.Throws<ArgumentException>(() => WindowsServiceCommands.Parse(["install", "--service-start-type", "sometimes"]));
    }

    [Test]
    public void BuildRuntimeArguments_WithPortAndUrls_Throws()
    {
        var command = WindowsServiceCommands.Parse(["install", "--port", "5090", "--", "--urls", "http://localhost:5089"]);

        Assert.Throws<ArgumentException>(() => WindowsServiceCommands.BuildRuntimeArguments(command!));
    }
}