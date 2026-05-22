using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class ChatModelProfileServiceTests
{
    [Test]
    public void ListProfiles_WithoutExplicitProfiles_ReturnsImplicitLegacyDefault()
    {
        var service = CreateService(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                Provider = "Ollama",
                OllamaModel = "gemma4:e4b",
                OllamaContextWindowTokens = 32768
            }
        });

        var profiles = service.ListProfiles();
        var defaultProfile = service.GetDefaultProfileForRoles([MemorySmithRoles.Viewer]);

        Assert.Multiple(() =>
        {
            Assert.That(profiles, Has.Count.EqualTo(1));
            Assert.That(profiles[0].IsImplicit, Is.True);
            Assert.That(profiles[0].IsDefault, Is.True);
            Assert.That(profiles[0].Provider, Is.EqualTo("Ollama"));
            Assert.That(profiles[0].Model, Is.EqualTo("gemma4:e4b"));
            Assert.That(profiles[0].ContextWindowTokens, Is.EqualTo(32768));
            Assert.That(defaultProfile, Is.Not.Null);
        });
    }

    [Test]
    public void GetDefaultProfileForRoles_UsesExplicitEnabledDefaultAndRoleFilter()
    {
        var service = CreateService(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                DefaultModelProfileId = "editor-athena",
                ModelProfiles =
                [
                    new ChatModelProfileOptions
                    {
                        Id = "viewer-quick",
                        Name = "Viewer Quick",
                        Provider = "GitHub",
                        Model = "gpt-4.1-mini",
                        Enabled = true,
                        AllowedRoles = [MemorySmithRoles.Viewer]
                    },
                    new ChatModelProfileOptions
                    {
                        Id = "editor-athena",
                        Name = "Athena",
                        Provider = "Ollama",
                        Model = "gemma4:e4b",
                        Enabled = true,
                        AllowedRoles = [MemorySmithRoles.Editor]
                    }
                ]
            }
        });

        var viewerDefault = service.GetDefaultProfileForRoles([MemorySmithRoles.Viewer]);
        var editorDefault = service.GetDefaultProfileForRoles([MemorySmithRoles.Editor]);
        var adminDefault = service.GetDefaultProfileForRoles([MemorySmithRoles.Admin]);

        Assert.Multiple(() =>
        {
            Assert.That(viewerDefault, Is.Null);
            Assert.That(editorDefault?.Id, Is.EqualTo("editor-athena"));
            Assert.That(adminDefault?.Id, Is.EqualTo("editor-athena"));
        });
    }

    private static ChatModelProfileService CreateService(MemorySmithOptions options)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new ChatModelProfileService(new StaticOptionsMonitor<MemorySmithOptions>(options), configuration, null!);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}