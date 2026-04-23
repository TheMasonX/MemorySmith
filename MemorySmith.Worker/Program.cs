using MemorySmith.Core.Indexing;
using MemorySmith.Storage;
using MemorySmith.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

// Storage
var dataPath = builder.Configuration["DataPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "Data", "Memories");
builder.Services.AddSingleton<IMemoryStore>(_ => new FileMemoryStore(dataPath));
builder.Services.AddSingleton<MemoryIndex>();

// Background services
builder.Services.AddHostedService<TriageService>();
builder.Services.AddHostedService<ConsolidationService>();
builder.Services.AddHostedService<IndexingService>();

// API
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();
