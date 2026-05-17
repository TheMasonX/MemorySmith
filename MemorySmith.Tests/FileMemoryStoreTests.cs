using MemorySmith.Core.Models;
using MemorySmith.Storage;
using NUnit.Framework;

namespace MemorySmith.Tests;

[TestFixture]
public class FileMemoryStoreTests
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
    public void SaveAndLoad_RoundTrips()
    {
        var record = new MemoryRecord { Id = "test-1", Content = "Hello", Title = "T1" };
        _store.Save(record);
        var loaded = _store.Load("test-1");
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Content, Is.EqualTo("Hello"));
        Assert.That(loaded.Title, Is.EqualTo("T1"));
    }

    [Test]
    public void Load_ReturnsNull_WhenNotFound()
    {
        var result = _store.Load("nonexistent");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Delete_RemovesRecord()
    {
        var record = new MemoryRecord { Id = "test-del" };
        _store.Save(record);
        _store.Delete("test-del");
        Assert.That(_store.Load("test-del"), Is.Null);
    }

    [Test]
    public void LoadAll_ReturnsAllSavedRecords()
    {
        for (int i = 0; i < 3; i++)
            _store.Save(new MemoryRecord { Id = $"r{i}", Content = $"Content {i}" });
        var all = _store.LoadAll().ToList();
        Assert.That(all, Has.Count.EqualTo(3));
    }

    [Test]
    public void Save_MovesFile_WhenStatusChanges()
    {
        var record = new MemoryRecord { Id = "test-move", Status = MemoryStatus.Unconsolidated };
        _store.Save(record);
        record.Status = MemoryStatus.Working;
        _store.Save(record);
        var loaded = _store.Load("test-move");
        Assert.That(loaded!.Status, Is.EqualTo(MemoryStatus.Working));
        // Old path should be gone
        var oldPath = Path.Combine(_tempDir, "Unconsolidated", "test-move.json");
        Assert.That(File.Exists(oldPath), Is.False);
    }
}
