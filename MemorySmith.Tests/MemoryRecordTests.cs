using MemorySmith.Core.Models;
using NUnit.Framework;

namespace MemorySmith.Tests;

[TestFixture]
public class MemoryRecordTests
{
    [Test]
    public void NewRecord_HasDefaultValues()
    {
        var record = new MemoryRecord();
        Assert.That(record.Status, Is.EqualTo(MemoryStatus.Unconsolidated));
        Assert.That(record.Tags, Is.Not.Null);
        Assert.That(record.References, Is.Not.Null);
        Assert.That(record.Conflicts, Is.Not.Null);
        Assert.That(record.Id, Is.Not.Empty);
    }

    [Test]
    public void Record_CanSetAllProperties()
    {
        var record = new MemoryRecord
        {
            Id = "test-1",
            Content = "Test content",
            Title = "Test title",
            Status = MemoryStatus.Working,
            Confidence = 0.8,
            UsageCount = 5
        };
        Assert.That(record.Id, Is.EqualTo("test-1"));
        Assert.That(record.Content, Is.EqualTo("Test content"));
        Assert.That(record.Status, Is.EqualTo(MemoryStatus.Working));
        Assert.That(record.Confidence, Is.EqualTo(0.8));
    }

    [Test]
    public void NewRecord_HasRelationshipsList()
    {
        var record = new MemoryRecord();
        Assert.That(record.Relationships, Is.Not.Null);
        Assert.That(record.Relationships, Is.Empty);
    }

    [Test]
    public void ReferencesAndConflicts_SurviveUnmodified_WhenUsingRelationships()
    {
        // Simulates the additive migration: existing data keeps References/Conflicts,
        // while new code can also populate Relationships.
        var record = new MemoryRecord
        {
            References = ["ref-1", "ref-2"],
            Conflicts = ["conflict-1"]
        };

        record.Relationships.Add(MemoryRelationshipEdge.Create("src-1", "ref-1", RelationType.References));
        record.Relationships.Add(MemoryRelationshipEdge.Create("src-1", "ref-2", RelationType.References));
        record.Relationships.Add(MemoryRelationshipEdge.Create("src-1", "conflict-1", RelationType.ConflictsWith));

        // Backward compatibility: original arrays are untouched
        Assert.That(record.References, Is.EquivalentTo(new[] { "ref-1", "ref-2" }));
        Assert.That(record.Conflicts, Is.EquivalentTo(new[] { "conflict-1" }));
        Assert.That(record.Relationships, Has.Count.EqualTo(3));
    }

    [Test]
    public void RelationshipEdge_Create_SetsDefaultOriginAndTimestamp()
    {
        var edge = MemoryRelationshipEdge.Create("src", "tgt", RelationType.DependsOn);
        Assert.That(edge.SourceId, Is.EqualTo("src"));
        Assert.That(edge.TargetId, Is.EqualTo("tgt"));
        Assert.That(edge.RelationType, Is.EqualTo(RelationType.DependsOn));
        Assert.That(edge.Origin, Is.EqualTo(EdgeOrigin.Manual));
        Assert.That(edge.CreatedAtUtc, Is.Not.EqualTo(default(DateTimeOffset)));
    }

    [Test]
    public void RelationshipEdge_SupportsAllRelationTypes()
    {
        var types = Enum.GetValues<RelationType>();
        Assert.That(types, Is.EquivalentTo(new[]
        {
            RelationType.References,
            RelationType.Supersedes,
            RelationType.SupersededBy,
            RelationType.DependsOn,
            RelationType.ConflictsWith,
            RelationType.Mentions,
            RelationType.LinksTo,
            RelationType.SameAs
        }));
    }
}
