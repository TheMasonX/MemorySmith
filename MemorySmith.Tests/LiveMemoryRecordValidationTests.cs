using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class LiveMemoryRecordValidationTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-live-memories-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void RepositoryMemoryFiles_DeserializeAndAlignWithFilesystemMetadata()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var errors = new List<string>();

        foreach (var file in EnumerateMemoryFiles(dataPath))
        {
            var relativePath = ToRelativePath(dataPath, file);
            if (!TryReadRecord(file, relativePath, errors, out var record) || record is null)
            {
                continue;
            }

            var expectedId = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(record.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{relativePath}: id '{record.Id}' does not match file name '{expectedId}'.");
            }

            var statusFolder = Path.GetFileName(Path.GetDirectoryName(file));
            if (!Enum.TryParse<MemoryStatus>(statusFolder, ignoreCase: true, out var expectedStatus))
            {
                errors.Add($"{relativePath}: status folder '{statusFolder}' is not recognized.");
                continue;
            }

            if (record.Status != expectedStatus)
            {
                errors.Add($"{relativePath}: status '{record.Status}' does not match folder '{expectedStatus}'.");
            }
        }

        AssertValidation(errors, "Live memory file contract validation");
    }

    [Test]
    public async Task RepositoryMemoryFiles_PassApplicationValidationContract()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var storageDiagnostics = new StorageDiagnostics();
        var store = new FileMemoryStore(dataPath, storageDiagnostics);
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            store,
            options);
        var service = TestServiceFactory.CreateMemoryApplicationService(
            store,
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher(),
            diagnostics: diagnostics);

        var errors = new List<string>();
        foreach (var file in EnumerateMemoryFiles(dataPath))
        {
            var relativePath = ToRelativePath(dataPath, file);
            if (!TryReadRecord(file, relativePath, errors, out var record) || record is null)
            {
                continue;
            }

            try
            {
                var result = await service.UpdateAsync(record.Id, CloneRecord(record), CancellationToken.None);
                if (result is null)
                {
                    errors.Add($"{relativePath}: record '{record.Id}' could not be loaded for validation.");
                }
            }
            catch (MemoryValidationException ex)
            {
                errors.Add($"{relativePath}: {FormatValidationErrors(ex)}");
            }
            catch (Exception ex)
            {
                errors.Add($"{relativePath}: unexpected validation failure: {ex.Message}");
            }
        }

        foreach (var corruptFile in storageDiagnostics.GetSnapshot().CorruptFiles)
        {
            errors.Add($"{ToRelativePath(dataPath, corruptFile.Path)}: corrupt file reported by storage diagnostics: {corruptFile.Error}");
        }

        AssertValidation(errors, "Live memory application validation");
    }

    private static IEnumerable<string> EnumerateMemoryFiles(string dataPath) =>
        Directory.EnumerateFiles(dataPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static string ToRelativePath(string dataPath, string filePath) =>
        Path.GetRelativePath(dataPath, filePath).Replace('\\', '/');

    private static bool TryReadRecord(string filePath, string relativePath, List<string> errors, out MemoryRecord? record)
    {
        try
        {
            record = JsonSerializer.Deserialize<MemoryRecord>(File.ReadAllText(filePath));
        }
        catch (Exception ex)
        {
            errors.Add($"{relativePath}: invalid JSON for MemoryRecord: {ex.Message}");
            record = null;
            return false;
        }

        if (record is null)
        {
            errors.Add($"{relativePath}: JSON deserialized to null.");
            return false;
        }

        return true;
    }

    private static MemoryRecord CloneRecord(MemoryRecord record) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Content = record.Content,
        Status = record.Status,
        Confidence = record.Confidence,
        Tags = [.. record.Tags],
        References = [.. record.References],
        Conflicts = [.. record.Conflicts],
        SourceLinks = record.SourceLinks.Select(link => new SourceLink
        {
            Label = link.Label,
            Uri = link.Uri,
            StartLine = link.StartLine,
            EndLine = link.EndLine
        }).ToList(),
        UsageCount = record.UsageCount,
        LastUpdated = record.LastUpdated
    };

    private static string FormatValidationErrors(MemoryValidationException exception) =>
        string.Join(" | ", exception.Errors
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}: {string.Join("; ", item.Value)}"));

    private static void AssertValidation(IReadOnlyCollection<string> errors, string validationName)
    {
        if (errors.Count == 0)
        {
            return;
        }

        Assert.Fail($"{validationName} found {errors.Count} issue(s):{Environment.NewLine} - {string.Join(Environment.NewLine + " - ", errors)}");
    }
}