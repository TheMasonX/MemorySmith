using MemorySmith.Core.Models;
using MemorySmith.Storage;
using NUnit.Framework;

namespace MemorySmith.Tests;

[TestFixture]
public class PaginationTests
{
    private string _tempDir = null!;
    private FileMemoryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _store = new FileMemoryStore(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public void LoadAll_WithMultipleRecords_CanPaginate()
    {
        for (int i = 0; i < 25; i++)
            _store.Save(new MemoryRecord { Id = $"r{i:D2}", Content = $"Content {i}" });

        var all = _store.LoadAll().ToList();
        Assert.That(all, Has.Count.EqualTo(25));

        var page1 = all.Skip(0).Take(20).ToList();
        var page2 = all.Skip(20).Take(20).ToList();
        Assert.That(page1, Has.Count.EqualTo(20));
        Assert.That(page2, Has.Count.EqualTo(5));
    }

    [Test]
    public void LoadAll_CanFilterByStatus()
    {
        _store.Save(new MemoryRecord { Id = "w1", Status = MemoryStatus.Working });
        _store.Save(new MemoryRecord { Id = "w2", Status = MemoryStatus.Working });
        _store.Save(new MemoryRecord { Id = "c1", Status = MemoryStatus.Core });

        var working = _store.LoadAll().Where(r => r.Status == MemoryStatus.Working).ToList();
        Assert.That(working, Has.Count.EqualTo(2));
        Assert.That(working.All(r => r.Status == MemoryStatus.Working), Is.True);
    }

    [Test]
    public void Save_UpdatedRecord_ReplacesOriginal()
    {
        var record = new MemoryRecord { Id = "upd-1", Content = "Original", Status = MemoryStatus.Unconsolidated };
        _store.Save(record);

        record.Content = "Updated";
        record.Status = MemoryStatus.Working;
        _store.Save(record);

        var loaded = _store.Load("upd-1");
        Assert.That(loaded!.Content, Is.EqualTo("Updated"));
        Assert.That(loaded.Status, Is.EqualTo(MemoryStatus.Working));

        // Should only exist in Working folder, not Unconsolidated
        var all = _store.LoadAll().ToList();
        Assert.That(all.Count(r => r.Id == "upd-1"), Is.EqualTo(1));
    }
}

[TestFixture]
public class StatsTests
{
    private string _tempDir = null!;
    private FileMemoryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _store = new FileMemoryStore(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public void Stats_CountsByStatus_AreCorrect()
    {
        _store.Save(new MemoryRecord { Id = "u1", Status = MemoryStatus.Unconsolidated, Confidence = 0.5 });
        _store.Save(new MemoryRecord { Id = "u2", Status = MemoryStatus.Unconsolidated, Confidence = 0.3 });
        _store.Save(new MemoryRecord { Id = "w1", Status = MemoryStatus.Working, Confidence = 0.7 });
        _store.Save(new MemoryRecord { Id = "c1", Status = MemoryStatus.Core, Confidence = 0.9 });

        var all = _store.LoadAll().ToList();
        var stats = new
        {
            Total = all.Count,
            Unconsolidated = all.Count(r => r.Status == MemoryStatus.Unconsolidated),
            Working = all.Count(r => r.Status == MemoryStatus.Working),
            Core = all.Count(r => r.Status == MemoryStatus.Core),
            AvgConfidence = all.Average(r => r.Confidence)
        };

        Assert.That(stats.Total, Is.EqualTo(4));
        Assert.That(stats.Unconsolidated, Is.EqualTo(2));
        Assert.That(stats.Working, Is.EqualTo(1));
        Assert.That(stats.Core, Is.EqualTo(1));
        Assert.That(stats.AvgConfidence, Is.EqualTo(0.6).Within(0.01));
    }

    [Test]
    public void MemoryMetadata_MapsCorrectly()
    {
        var record = new MemoryRecord
        {
            Id = "meta-1", Title = "Test", Status = MemoryStatus.Core,
            Confidence = 0.8, Tags = new List<string> { "a", "b" }, UsageCount = 5
        };
        var meta = new MemoryMetadata
        {
            Id = record.Id, Title = record.Title, Status = record.Status,
            Confidence = record.Confidence, Tags = record.Tags,
            UsageCount = record.UsageCount, LastUpdated = record.LastUpdated
        };

        Assert.That(meta.Id, Is.EqualTo("meta-1"));
        Assert.That(meta.Tags, Has.Count.EqualTo(2));
        Assert.That(meta.Confidence, Is.EqualTo(0.8));
    }
}
