using MemorySmith.App.Services;

namespace MemorySmith.Tests;

[TestFixture]
public class PageNavigationTreeBuilderTests
{
    [Test]
    public void Build_CreatesDirectoryTreeFromNestedSlugs()
    {
        var pages = new[]
        {
            new PageSummary("operations/deploy/service-restart", "Service Restart", "", DateTime.UtcNow),
            new PageSummary("operations/deploy/rollback", "Rollback", "", DateTime.UtcNow),
            new PageSummary("architecture", "Architecture", "", DateTime.UtcNow)
        };

        var tree = PageNavigationTreeBuilder.Build(pages);

        Assert.Multiple(() =>
        {
            Assert.That(tree, Has.Count.EqualTo(2));
            Assert.That(tree[0].Label, Is.EqualTo("Operations"));
            Assert.That(tree[0].PageCount, Is.EqualTo(2));
            Assert.That(tree[0].Children.Single().Label, Is.EqualTo("Deploy"));
            Assert.That(tree[0].Children.Single().Children.Select(child => child.Label), Is.EqualTo(new[] { "Rollback", "Service Restart" }));
            Assert.That(tree[1].Slug, Is.EqualTo("architecture"));
        });
    }

    [Test]
    public void Flatten_OnlyIncludesExpandedFolderChildren()
    {
        var pages = new[]
        {
            new PageSummary("operations/deploy/service-restart", "Service Restart", "", DateTime.UtcNow),
            new PageSummary("architecture", "Architecture", "", DateTime.UtcNow)
        };

        var tree = PageNavigationTreeBuilder.Build(pages);
        var collapsed = PageNavigationTreeBuilder.Flatten(tree, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var expanded = PageNavigationTreeBuilder.Flatten(tree, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "operations",
            "operations/deploy"
        });

        Assert.Multiple(() =>
        {
            Assert.That(collapsed.Select(row => row.Node.Label), Is.EqualTo(new[] { "Operations", "Architecture" }));
            Assert.That(expanded.Select(row => row.Node.Label), Is.EqualTo(new[] { "Operations", "Deploy", "Service Restart", "Architecture" }));
            Assert.That(expanded.Single(row => row.Node.Key == "operations/deploy").Depth, Is.EqualTo(1));
            Assert.That(expanded.Single(row => row.Node.Slug == "operations/deploy/service-restart").Depth, Is.EqualTo(2));
        });
    }

    [Test]
    public void AncestorKeysForSlug_ReturnsFolderAncestorsForSelectedPage()
    {
        var pages = new[]
        {
            new PageSummary("notes/intro/getting-started", "Getting Started", "", DateTime.UtcNow),
            new PageSummary("notes/faq", "FAQ", "", DateTime.UtcNow)
        };

        var tree = PageNavigationTreeBuilder.Build(pages);
        var ancestors = PageNavigationTreeBuilder.AncestorKeysForSlug(tree, "notes/intro/getting-started");

        Assert.That(ancestors, Is.EquivalentTo(new[] { "notes", "notes/intro" }));
    }
}