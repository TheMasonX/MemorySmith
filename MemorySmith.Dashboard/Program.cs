using MemorySmith.Dashboard.Components;
using MemorySmith.Dashboard.Services;
using MudBlazor.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddScoped<DashboardState>();

// Configure detailed errors for debugging
if (builder.Environment.IsDevelopment())
{
    builder.Services.Configure<CircuitOptions>(options =>
    {
        options.DetailedErrors = true;
    });
}

// Typed HttpClient for Worker API
var workerApiBaseUrl = builder.Configuration["WorkerApiBaseUrl"] ?? "http://localhost:5196";
var workerApiTimeoutSeconds = builder.Configuration.GetValue<int?>("WorkerApiTimeoutSeconds") ?? 10;
if (workerApiTimeoutSeconds <= 0)
{
    workerApiTimeoutSeconds = 10;
}

builder.Services.AddHttpClient<MemoryApiClient>(client =>
{
    client.BaseAddress = new Uri(workerApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(workerApiTimeoutSeconds);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
