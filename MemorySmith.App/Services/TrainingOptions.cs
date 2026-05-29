namespace MemorySmith.App.Services;

public sealed class TrainingOptions
{
    public bool ChatTranscriptEnabled { get; set; }
    public bool StoreChatContent { get; set; }
    public string TranscriptDirectory { get; set; } = Path.Combine("..", "Data", "Events", "chat-transcripts");
    public int TranscriptRetentionDays { get; set; } = 90;
    public bool TranscriptRedactionEnabled { get; set; } = true;
    public bool FeedbackEnabled { get; set; }
    public string ActiveModelTag { get; set; } = string.Empty;
    public string FallbackModelTag { get; set; } = string.Empty;
    public string TrainingDataExportPath { get; set; } = Path.Combine("..", "Data", "Training", "exports");
    public string RunsDirectory { get; set; } = Path.Combine("..", "runs");
    public string PythonVenvPath { get; set; } = Path.Combine(".venv");
    public string PythonHarnessScript { get; set; } = Path.Combine("MemorySmith.Training", "harness.py");
    public int MaxRunMinutes { get; set; } = 360;
    public PreferenceExportFormat PreferenceFormat { get; set; } = PreferenceExportFormat.FilteredSft;
    public List<string> ExcludePrincipalIds { get; set; } = [];
    public double MinObjective1Score { get; set; } = 0.85;
    public double MinObjective2Score { get; set; } = 0.80;
    public int MaxRegressions { get; set; } = 5;
    public bool ShadowEvalEnabled { get; set; }
}

public enum PreferenceExportFormat
{
    FilteredSft = 0,
    Dpo = 1,
    Orpo = 2
}
