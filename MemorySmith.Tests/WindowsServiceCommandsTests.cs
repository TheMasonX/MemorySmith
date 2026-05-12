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
            Assert.That(command.RuntimeArguments, Is.Empty);
        });
    }

    [Test]
    public void Parse_WithUninstallFlagAndName_ReturnsUninstallCommand()
    {
        var command = WindowsServiceCommands.Parse(["--uninstall-service", "--service-name", "MemorySmith.Dev"]);

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
        });
    }
}