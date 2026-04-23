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
}
