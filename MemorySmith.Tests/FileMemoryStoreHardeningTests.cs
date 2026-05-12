using MemorySmith.Core.Models;
using MemorySmith.Storage;

namespace MemorySmith.Tests;

[TestFixture]
public class FileMemoryStoreHardeningTests
{
    private string _tempDir = null!;
    private StorageDiagnostics _diagnostics = null!;
    private FileMemoryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-store-{Guid.NewGuid():N}");
        _diagnostics = new StorageDiagnostics();
        _store = new FileMemoryStore(_tempDir, _diagnostics);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void LoadAll_ReturnsStableSnapshot_WhenFilesChangeAfterCall()
    {
        _store.Save(new MemoryRecord { Id = "before", Content = "Before" });

        var snapshot = _store.LoadAll();
        _store.Save(new MemoryRecord { Id = "after", Content = "After" });

        Assert.That(snapshot.Select(x => x.Id), Is.EqualTo(new[] { "before" }));
    }

    [Test]
    public void LoadAll_RecordsCorruptFileDiagnosticsAndSkipsBadFile()
    {
        var corruptPath = Path.Combine(_tempDir, MemoryStatus.Working.ToString(), "corrupt.json");
        File.WriteAllText(corruptPath, "{ this is not json");
        _store.Save(new MemoryRecord { Id = "valid", Content = "Valid" });

        var records = _store.LoadAll().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(records.Select(x => x.Id), Is.EqualTo(new[] { "valid" }));
            Assert.That(_diagnostics.GetSnapshot().CorruptFiles, Has.Count.EqualTo(1));
            Assert.That(_diagnostics.GetSnapshot().CorruptFiles.Single().Path, Is.EqualTo(corruptPath));
        });
    }

    [Test]
    public async Task ConcurrentSaveDeleteLoadAll_DoesNotThrowOrEscapeBasePath()
    {
        var tasks = Enumerable.Range(0, 20).Select(async worker =>
        {
            for (var i = 0; i < 50; i++)
            {
                var id = $"worker-{worker}-record-{i}";
                _store.Save(new MemoryRecord { Id = id, Content = $"Content {i}", Status = (MemoryStatus)(i % 4) });
                _ = _store.LoadAll().ToList();
                if (i % 3 == 0)
                {
                    _store.Delete(id);
                }
                await Task.Yield();
            }
        });

        Assert.DoesNotThrowAsync(async () => await Task.WhenAll(tasks));

        var files = Directory.EnumerateFiles(_tempDir, "*.json", SearchOption.AllDirectories).ToList();
        Assert.That(files, Is.All.StartsWith(_tempDir));
    }
}