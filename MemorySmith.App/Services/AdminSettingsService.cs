using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed class AdminSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly IReadOnlyList<string> DayOfWeekOptions = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly AuditLogService _audit;
    private readonly string _settingsPath;
    private readonly IReadOnlyList<EditableSettingDescriptor> _editableSettings;

    public AdminSettingsService(IOptionsMonitor<MemorySmithOptions> options, IConfiguration configuration, AuditLogService audit)
    {
        _options = options;
        _configuration = configuration;
        _audit = audit;
        _settingsPath = MemorySmithConfigurationPaths.ResolveSettingsOverridePath(configuration["MemorySmith:SettingsOverridePath"]);
        _editableSettings = BuildEditableSettings();
    }

    public IReadOnlyList<AdminSettingItem> ListEditableSettings()
    {
        var settings = _options.CurrentValue;
        return _editableSettings
            .Select(descriptor => descriptor.ToItem(settings))
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AdminSettingUpdateResult> UpdateAsync(AdminSettingUpdateRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _editableSettings.FirstOrDefault(item => string.Equals(item.Key, request.Key, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return new AdminSettingUpdateResult(false, "This setting cannot be edited from the admin UI.");
        }

        if (!descriptor.TryConvert(request.Value, out var convertedValue, out var error))
        {
            return new AdminSettingUpdateResult(false, error ?? "The setting value is invalid.");
        }

        JsonObject root;
        try
        {
            root = await LoadSettingsRootAsync(cancellationToken);
        }
        catch (JsonException)
        {
            return new AdminSettingUpdateResult(false, "The local settings file is not valid JSON.");
        }

        SetJsonValue(root, descriptor.Key.Split(':'), convertedValue);
        if (!TryValidateCrossSettingConstraints(root, out var crossSettingError))
        {
            return new AdminSettingUpdateResult(false, crossSettingError ?? "The setting combination is invalid.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var tempPath = _settingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, root.ToJsonString(JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(tempPath, _settingsPath, overwrite: true);

        if (_configuration is IConfigurationRoot rootConfiguration)
        {
            rootConfiguration.Reload();
        }

        await _audit.RecordAsync(
            "settings.updated",
            "Setting",
            descriptor.Key,
            MemorySmithAuditOutcomes.Success,
            details: new { descriptor.Key, Value = RedactedSettingValue(descriptor, convertedValue) },
            cancellationToken: cancellationToken);

        return new AdminSettingUpdateResult(true, null);
    }

    private static string? RedactedSettingValue(EditableSettingDescriptor descriptor, object convertedValue)
    {
        if (!descriptor.IsSensitive)
        {
            return Convert.ToString(convertedValue, CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(Convert.ToString(convertedValue, CultureInfo.InvariantCulture))
            ? "Cleared"
            : "Configured";
    }

    private bool TryValidateCrossSettingConstraints(JsonObject root, out string? error)
    {
        error = null;

        var current = _options.CurrentValue.CodeSearch;
        var hybridVectorWeight = ResolveCodeSearchDouble(root, "HybridVectorWeight", current.HybridVectorWeight);
        var hybridLexicalWeight = ResolveCodeSearchDouble(root, "HybridLexicalWeight", current.HybridLexicalWeight);
        var minCoverageWeight = ResolveCodeSearchDouble(root, "MinTokenCoverageWeight", current.MinTokenCoverageWeight);
        var maxCoverageWeight = ResolveCodeSearchDouble(root, "MaxTokenCoverageWeight", current.MaxTokenCoverageWeight);

        if (hybridVectorWeight <= 0 && hybridLexicalWeight <= 0)
        {
            error = "Code-search hybrid vector and lexical weights cannot both be zero.";
            return false;
        }

        if (minCoverageWeight > maxCoverageWeight)
        {
            error = "Code-search min token coverage weight must be less than or equal to max token coverage weight.";
            return false;
        }

        return true;
    }

    private static double ResolveCodeSearchDouble(JsonObject root, string codeSearchFieldName, double currentValue)
    {
        if (!TryGetJsonValue(root, out var value, "MemorySmith", "CodeSearch", codeSearchFieldName))
        {
            return currentValue;
        }

        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out var number) => number,
            JsonValue jsonValue when jsonValue.TryGetValue<float>(out var number) => number,
            JsonValue jsonValue when jsonValue.TryGetValue<decimal>(out var number) => (double)number,
            JsonValue jsonValue when jsonValue.TryGetValue<int>(out var number) => number,
            JsonValue jsonValue when jsonValue.TryGetValue<long>(out var number) => number,
            _ => currentValue
        };
    }

    private static bool TryGetJsonValue(JsonObject root, out JsonNode? value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment, out value) || value is null)
            {
                value = null;
                return false;
            }
        }

        return value is not null;
    }

    private async Task<JsonObject> LoadSettingsRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new JsonObject();
        }

        await using var stream = File.OpenRead(_settingsPath);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return node as JsonObject ?? new JsonObject();
    }

    private static void SetJsonValue(JsonObject root, IReadOnlyList<string> path, object value)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        current[path[^1]] = value switch
        {
            null => null,
            bool boolean => JsonValue.Create(boolean),
            int integer => JsonValue.Create(integer),
            long longValue => JsonValue.Create(longValue),
            double doubleValue => JsonValue.Create(doubleValue),
            float floatValue => JsonValue.Create(floatValue),
            decimal decimalValue => JsonValue.Create(decimalValue),
            IReadOnlyList<string> strings => new JsonArray(strings.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }

    private static IReadOnlyList<EditableSettingDescriptor> BuildEditableSettings() =>
    [
        EditableSettingDescriptor.String("MemorySmith:DataPath", "Memory records path", "Core paths", settings => settings.DataPath, 500, "Filesystem path that stores structured MemoryRecord JSON files. Changing it moves the active memory wiki root for app/API/search operations and normally requires the target folder to already contain or receive the intended records."),
        EditableSettingDescriptor.String("MemorySmith:PagesPath", "Markdown pages path", "Core paths", settings => settings.PagesPath, 500, "Filesystem path that stores markdown-backed wiki pages and page assets. Chat, page search, and the /pages UI read from this location after configuration reload."),
        EditableSettingDescriptor.String("MemorySmith:EventLogPath", "Event log path", "Core paths", settings => settings.EventLogPath, 500, "Path for the legacy file event log used by memory events. Keep it under Data/Events for local-first backups and avoid pointing it at a shared or sensitive directory."),
        EditableSettingDescriptor.String("MemorySmith:VarsPath", "Variables path", "Core paths", settings => settings.VarsPath, 500, "Path to vars.json, the variable map used by source links such as %MemorySmithRepo%. Source-link reading and open-with-default-app checks depend on these variables."),
        EditableSettingDescriptor.String("MemorySmith:DataProtectionKeysPath", "Data protection keys path", "Core paths", settings => settings.DataProtectionKeysPath, 500, "Directory where ASP.NET Core data-protection keys are persisted for auth cookies and encrypted state. Changing this can sign users out and should be stable for a deployed instance."),
        EditableSettingDescriptor.Integer("MemorySmith:Blazor:MaximumReceiveMessageSizeBytes", "Blazor max receive bytes", "Core paths", settings => (int)Math.Min(settings.Blazor.MaximumReceiveMessageSizeBytes, int.MaxValue), 65536, 67108864, "Maximum SignalR payload size accepted by the interactive server circuit. Larger values help with big page edits/uploads but usually require an app restart before the circuit limit changes."),
        EditableSettingDescriptor.Choice("MemorySmith:SecurityProfile", "Security profile", "Security", settings => MemorySmithSecurityProfiles.Normalize(settings.SecurityProfile), MemorySmithSecurityProfiles.All, "Preset for related security defaults: local-dev for permissive local iteration, secure-local for dogfood/local default, and remote-hardened for exposed instances. Explicit settings still appear separately for review."),
        EditableSettingDescriptor.Boolean("MemorySmith:AllowRemoteApi", "Allow remote API", "Security", settings => settings.AllowRemoteApi, "Allows non-loopback HTTP API/MCP requests through the request guard. Remote API/MCP requests are blocked until a shared API key is configured; keep disabled unless the instance also has a network boundary and intentional remote access policy."),
        EditableSettingDescriptor.String("MemorySmith:ApiKey", "Shared API key", "Security", settings => settings.ApiKey, 500, "Write-only shared API key accepted by API and MCP requests when configured. Required for non-loopback API/MCP access when Allow remote API is enabled. The UI never echoes the existing value; enter a new value only when rotating or clearing the key.", isSensitive: true),
        EditableSettingDescriptor.Boolean("MemorySmith:ContentSecurityPolicyEnabled", "Enable Content-Security-Policy header", "Security", settings => settings.ContentSecurityPolicyEnabled, "Emits the configured Content-Security-Policy response header on app requests. Keep enabled for defense-in-depth against script injection."),
        EditableSettingDescriptor.String("MemorySmith:ContentSecurityPolicy", "Content-Security-Policy value", "Security", settings => settings.ContentSecurityPolicy, 4000, "Raw Content-Security-Policy header value applied when CSP is enabled. Tune directives for deployment-specific script/style/connect requirements."),
        EditableSettingDescriptor.Boolean("MemorySmith:XContentTypeOptionsEnabled", "Enable X-Content-Type-Options header", "Security", settings => settings.XContentTypeOptionsEnabled, "Adds the X-Content-Type-Options response header for MIME-sniffing defense on browsers."),
        EditableSettingDescriptor.String("MemorySmith:XContentTypeOptions", "X-Content-Type-Options value", "Security", settings => settings.XContentTypeOptions, 100, "Response value for X-Content-Type-Options. Use nosniff unless a legacy client requires custom behavior."),
        EditableSettingDescriptor.Boolean("MemorySmith:ReferrerPolicyEnabled", "Enable Referrer-Policy header", "Security", settings => settings.ReferrerPolicyEnabled, "Adds Referrer-Policy response header control for outbound referrer disclosure."),
        EditableSettingDescriptor.String("MemorySmith:ReferrerPolicy", "Referrer-Policy value", "Security", settings => settings.ReferrerPolicy, 100, "Response value for Referrer-Policy. strict-origin-when-cross-origin is a practical default for local and LAN deployments."),
        EditableSettingDescriptor.Boolean("MemorySmith:XFrameOptionsEnabled", "Enable X-Frame-Options header", "Security", settings => settings.XFrameOptionsEnabled, "Adds X-Frame-Options clickjacking defense header on responses."),
        EditableSettingDescriptor.String("MemorySmith:XFrameOptions", "X-Frame-Options value", "Security", settings => settings.XFrameOptions, 40, "Response value for X-Frame-Options. Use DENY unless same-origin framing is explicitly required."),
        EditableSettingDescriptor.Boolean("MemorySmith:PermissionsPolicyEnabled", "Enable Permissions-Policy header", "Security", settings => settings.PermissionsPolicyEnabled, "Adds Permissions-Policy response header to constrain browser feature access."),
        EditableSettingDescriptor.String("MemorySmith:PermissionsPolicy", "Permissions-Policy value", "Security", settings => settings.PermissionsPolicy, 1000, "Response value for Permissions-Policy. Keep restrictive defaults unless a route explicitly needs additional browser capabilities."),

        EditableSettingDescriptor.Choice("MemorySmith:Database:Provider", "Database provider", "Database", settings => settings.Database.Provider, ["SQLite"], "Database backend used by the active app host. SQLite is the supported provider in this repository; changing this is a deployment-level operation."),
        EditableSettingDescriptor.String("MemorySmith:Database:ConnectionString", "Database connection string", "Database", settings => settings.Database.ConnectionString, 1000, "Write-only SQLite connection string for users, roles, provider links, audit logs, and version history. Changing it points the app at a different metadata database and normally requires restart/revalidation.", isSensitive: true),
        EditableSettingDescriptor.Boolean("MemorySmith:Database:ApplyMigrationsOnStartup", "Apply migrations on startup", "Database", settings => settings.Database.ApplyMigrationsOnStartup, "Runs SQLite metadata migrations when the app starts so auth, audit, and history tables match the current code. Disable only for tightly controlled deployment flows."),
        EditableSettingDescriptor.Boolean("MemorySmith:Database:UseWal", "Use SQLite WAL", "Database", settings => settings.Database.UseWal, "Enables SQLite write-ahead logging for better concurrent reads and writes in the single-host app. Disable only if the storage environment cannot support WAL sidecar files."),
        EditableSettingDescriptor.Integer("MemorySmith:Database:BusyTimeoutSeconds", "SQLite busy timeout seconds", "Database", settings => settings.Database.BusyTimeoutSeconds, 1, 600, "Maximum time SQLite waits for a locked database before failing metadata operations. Increase for slow disks or concurrent local activity; keep modest so UI/API failures surface promptly."),

        EditableSettingDescriptor.Boolean("MemorySmith:Auth:Enabled", "Authentication enabled", "Auth", settings => settings.Auth.Enabled, "Controls whether interactive sign-in is enabled. Privileged admin APIs still require explicit authenticated Admin claims even when this is false, so do not use it as an admin bypass."),
        EditableSettingDescriptor.Choice("MemorySmith:Auth:AnonymousAccess", "Anonymous access", "Auth", settings => settings.Auth.AnonymousAccess, ["None", MemorySmithRoles.Viewer], "Role granted to signed-out visitors for non-admin pages. Admin/setting/audit/restore policies clamp anonymous users away from privileged access even if the config is accidentally too broad."),
        EditableSettingDescriptor.Choice("MemorySmith:Auth:AuthenticatedDefaultRole", "Default signed-in role", "Auth", settings => MemorySmithPermissionHandler.NormalizeAuthenticatedDefaultRole(settings.Auth.AuthenticatedDefaultRole), [MemorySmithRoles.Viewer, MemorySmithRoles.Editor], "Role assigned to newly signed-in users when they do not already have an explicit role. Admin is intentionally not an allowed default; promote admins from the Users tab."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:AutoEditorForAuthenticatedUsers", "Auto editor for signed-in users", "Auth", settings => settings.Auth.AutoEditorForAuthenticatedUsers, "Treats authenticated users as editors for normal wiki editing flows. It does not grant Admin privileges, settings access, audit access, or restore permissions."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:LocalPasswordEnabled", "Local password sign-in", "Auth", settings => settings.Auth.LocalPasswordEnabled, "Enables MemorySmith's built-in username/password provider alongside external providers. Disable only when external provider login is fully configured and tested."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:AllowAdminCreateLocalUsers", "Allow admin-created local users", "Auth", settings => settings.Auth.AllowAdminCreateLocalUsers, "Allows admins to create additional local password accounts for testing or controlled operator use from the Users tab. Keep disabled by default unless you intentionally want local-only accounts beyond the bootstrap admin."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:RequireHttpsForRemoteAuth", "Require HTTPS for remote auth", "Auth", settings => settings.Auth.RequireHttpsForRemoteAuth, "Blocks remote authentication flows over plain HTTP while still allowing loopback development. Keep enabled for any non-local deployment to protect cookies and OAuth redirects."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:OpenLocalEditorCompatibility", "Pre-setup local write compatibility", "Auth", settings => settings.Auth.OpenLocalEditorCompatibility, "Preserves local editor compatibility before first-admin setup. This does not satisfy Admin policies after setup and should remain a bootstrap compatibility valve only."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:Setup:AllowLoopbackBootstrap", "Allow loopback bootstrap", "Auth", settings => settings.Auth.Setup.AllowLoopbackBootstrap, "Allows first-admin setup from loopback when no admin exists. Disable after provisioning in stricter deployments that require token-based bootstrap only."),
        EditableSettingDescriptor.String("MemorySmith:Auth:Setup:BootstrapTokenHash", "Bootstrap token hash", "Auth", settings => settings.Auth.Setup.BootstrapTokenHash, 500, "Write-only hash used to authorize first-admin setup when loopback bootstrap is unavailable or undesired. Store a hash, not the raw bootstrap token.", isSensitive: true),
        EditableSettingDescriptor.Integer("MemorySmith:Auth:RateLimits:LoginPermitLimit", "Login permit limit", "Auth", settings => settings.Auth.RateLimits.LoginPermitLimit, 1, 1000, "Maximum failed local login attempts allowed inside the rate-limit window before throttling/lockout behavior starts."),
        EditableSettingDescriptor.Integer("MemorySmith:Auth:RateLimits:LoginWindowMinutes", "Login window minutes", "Auth", settings => settings.Auth.RateLimits.LoginWindowMinutes, 1, 1440, "Length of the rolling window used to count failed login attempts for the local password provider."),
        EditableSettingDescriptor.Integer("MemorySmith:Auth:RateLimits:LockoutMinutes", "Base lockout minutes", "Auth", settings => settings.Auth.RateLimits.LockoutMinutes, 1, 1440, "Initial lockout duration after repeated failed login attempts. This is the first step before progressive lockout extends the delay."),
        EditableSettingDescriptor.Integer("MemorySmith:Auth:RateLimits:MaxProgressiveLockoutMinutes", "Max progressive lockout minutes", "Auth", settings => settings.Auth.RateLimits.MaxProgressiveLockoutMinutes, 1, 10080, "Upper bound for progressive lockouts after repeated local password failures. Keep high enough to slow attacks but low enough for recoverable local administration."),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:Providers:GitHub:Enabled", "GitHub OAuth enabled", "Auth providers", settings => settings.Auth.Providers.GitHub.Enabled, "Enables GitHub OAuth login when a GitHub client id/secret and callback URL are configured in the provider application."),
        EditableSettingDescriptor.String("MemorySmith:Auth:Providers:GitHub:ClientId", "GitHub client id", "Auth providers", settings => settings.Auth.Providers.GitHub.ClientId, 500, "Write-only OAuth client id for GitHub sign-in. Treat it as deployment configuration and rotate it from the provider portal if exposed.", isSensitive: true),
        EditableSettingDescriptor.String("MemorySmith:Auth:Providers:GitHub:ClientSecret", "GitHub client secret", "Auth providers", settings => settings.Auth.Providers.GitHub.ClientSecret, 500, "Write-only OAuth client secret for GitHub sign-in. The existing value is never echoed; enter a new secret only when configuring or rotating credentials.", isSensitive: true),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:Providers:Google:Enabled", "Google OAuth enabled", "Auth providers", settings => settings.Auth.Providers.Google.Enabled, "Enables Google OAuth login when the Google OAuth client is configured with the MemorySmith callback URL."),
        EditableSettingDescriptor.String("MemorySmith:Auth:Providers:Google:ClientId", "Google client id", "Auth providers", settings => settings.Auth.Providers.Google.ClientId, 500, "Write-only OAuth client id for Google sign-in. Keep it aligned with the provider settings shown in the Admin Providers tab.", isSensitive: true),
        EditableSettingDescriptor.String("MemorySmith:Auth:Providers:Google:ClientSecret", "Google client secret", "Auth providers", settings => settings.Auth.Providers.Google.ClientSecret, 500, "Write-only OAuth client secret for Google sign-in. The UI masks the current value and writes only a replacement that you enter.", isSensitive: true),
        EditableSettingDescriptor.Boolean("MemorySmith:Auth:Providers:Microsoft:Enabled", "Microsoft OAuth enabled", "Auth providers", settings => settings.Auth.Providers.Microsoft.Enabled, "Enables Microsoft identity login when an Entra app registration is configured for this local MemorySmith instance."),
        EditableSettingDescriptor.String("MemorySmith:Auth:Providers:Microsoft:ClientId", "Microsoft client id", "Auth providers", settings => settings.Auth.Providers.Microsoft.ClientId, 500, "Write-only Entra application client id for Microsoft sign-in. Confirm redirect URIs before enabling the provider.", isSensitive: true),
        EditableSettingDescriptor.String("MemorySmith:Auth:Providers:Microsoft:ClientSecret", "Microsoft client secret", "Auth providers", settings => settings.Auth.Providers.Microsoft.ClientSecret, 500, "Write-only Entra client secret for Microsoft sign-in. Rotate from Entra and paste the new value here; the old value is never displayed.", isSensitive: true),

        EditableSettingDescriptor.Boolean("MemorySmith:Audit:JsonlEnabled", "JSONL audit enabled", "Audit", settings => settings.Audit.JsonlEnabled, "Writes append-only JSONL audit entries in addition to database-backed audit records. Keep enabled when you want file-level audit history for backups and external review."),
        EditableSettingDescriptor.String("MemorySmith:Audit:JsonlPath", "JSONL audit path", "Audit", settings => settings.Audit.JsonlPath, 500, "Path template for rotated audit JSONL files. Supports year/week tokens in the configured pattern and should usually stay under Data/Events."),
        EditableSettingDescriptor.String("MemorySmith:Audit:JsonlRotation", "JSONL audit rotation", "Audit", settings => settings.Audit.JsonlRotation, 100, "Rotation label used by the file audit writer. The default Weekly pattern keeps audit files manageable while preserving chronological review boundaries."),
        EditableSettingDescriptor.Boolean("MemorySmith:Audit:CompressRotatedLogs", "Compress rotated logs", "Audit", settings => settings.Audit.CompressRotatedLogs, "Compresses audit files after rotation to reduce local disk usage. Disable only if downstream tooling requires plain JSONL files."),
        EditableSettingDescriptor.Boolean("MemorySmith:Audit:HashChainEnabled", "Audit hash chain", "Audit", settings => settings.Audit.HashChainEnabled, "Adds tamper-evident hash chaining to audit log entries where supported. Keep enabled for stronger audit integrity signals."),

        EditableSettingDescriptor.Choice("MemorySmith:Logging:MinimumLevel", "Minimum log level", "Logging", settings => settings.Logging.MinimumLevel, ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"], "Minimum Serilog level emitted to configured sinks."),
        EditableSettingDescriptor.Boolean("MemorySmith:Logging:EnableConsole", "Enable console sink", "Logging", settings => settings.Logging.EnableConsole, "Enables Serilog console output for local terminals/service logs."),
        EditableSettingDescriptor.Boolean("MemorySmith:Logging:EnableStructuredFile", "Enable structured file sink", "Logging", settings => settings.Logging.EnableStructuredFile, "Writes structured JSONL application logs for diagnostics search and trend analysis."),
        EditableSettingDescriptor.String("MemorySmith:Logging:StructuredFilePath", "Structured file path", "Logging", settings => settings.Logging.StructuredFilePath, 500, "Path template for structured JSONL logs. Relative paths are resolved under the app base directory."),
        EditableSettingDescriptor.Integer("MemorySmith:Logging:StructuredFileRetainedDays", "Structured retention days", "Logging", settings => settings.Logging.StructuredFileRetainedDays, 1, 365, "Maximum retained rolling structured-log files."),
        EditableSettingDescriptor.Boolean("MemorySmith:Logging:WindowsEventLogEnabled", "Windows Event Log enabled", "Logging", settings => settings.Logging.WindowsEventLogEnabled, "On Windows, writes warning/error events to Windows Event Log. Enabled by default; disable only if local policy requires it."),
        EditableSettingDescriptor.String("MemorySmith:Logging:WindowsEventLogSource", "Windows Event Log source", "Logging", settings => settings.Logging.WindowsEventLogSource, 200, "Event source name used for Windows Event Log writes."),
        EditableSettingDescriptor.String("MemorySmith:Logging:WindowsEventLogName", "Windows Event Log name", "Logging", settings => settings.Logging.WindowsEventLogName, 100, "Event log channel name, typically Application."),
        EditableSettingDescriptor.Boolean("MemorySmith:Logging:RequestLoggingEnabled", "Request logging enabled", "Logging", settings => settings.Logging.RequestLoggingEnabled, "Enables structured HTTP request logging middleware."),
        EditableSettingDescriptor.Integer("MemorySmith:Logging:SlowRequestThresholdMs", "Slow request threshold (ms)", "Logging", settings => settings.Logging.SlowRequestThresholdMs, 1, 600000, "Requests at or above this duration are logged at warning level."),
        EditableSettingDescriptor.Boolean("MemorySmith:Logging:BenchmarkLoggingEnabled", "Benchmark logging enabled", "Logging", settings => settings.Logging.BenchmarkLoggingEnabled, "Emits benchmark-style timing events for critical app operations."),
        EditableSettingDescriptor.Integer("MemorySmith:Logging:BenchmarkSlowThresholdMs", "Benchmark slow threshold (ms)", "Logging", settings => settings.Logging.BenchmarkSlowThresholdMs, 1, 600000, "Benchmark timings at or above this threshold are treated as slow-path events."),
        EditableSettingDescriptor.Integer("MemorySmith:Logging:MetricsWindowDays", "Metrics window days", "Logging", settings => settings.Logging.MetricsWindowDays, 1, 365, "Default day window for log-derived metrics and trend charts."),
        EditableSettingDescriptor.Integer("MemorySmith:Logging:MetricsSampleLimit", "Metrics sample limit", "Logging", settings => settings.Logging.MetricsSampleLimit, 100, 50000, "Maximum number of log entries sampled when computing latency/error trend metrics."),
        EditableSettingDescriptor.Integer("MemorySmith:Logging:MaxDiagnosticsLogResults", "Diagnostics max log results", "Logging", settings => settings.Logging.MaxDiagnosticsLogResults, 10, 5000, "Maximum log entries returned from diagnostics log-search endpoints."),

        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:Enabled", "Telemetry enabled", "Telemetry", settings => settings.Telemetry.Enabled, "Enables OpenTelemetry instrumentation for traces/metrics with local-first defaults."),
        EditableSettingDescriptor.String("MemorySmith:Telemetry:ServiceName", "Telemetry service name", "Telemetry", settings => settings.Telemetry.ServiceName, 150, "Logical OTel service name reported in traces and metrics resources."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:TracingEnabled", "Tracing enabled", "Telemetry", settings => settings.Telemetry.TracingEnabled, "Enables OpenTelemetry trace instrumentation."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:MetricsEnabled", "Metrics enabled", "Telemetry", settings => settings.Telemetry.MetricsEnabled, "Enables OpenTelemetry metrics instrumentation."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:InstrumentMemoryOperations", "Instrument memory operations", "Telemetry", settings => settings.Telemetry.InstrumentMemoryOperations, "Emits custom low-cardinality MemorySmith operation spans and metrics."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:AspNetCoreInstrumentationEnabled", "ASP.NET Core instrumentation", "Telemetry", settings => settings.Telemetry.AspNetCoreInstrumentationEnabled, "Instruments inbound HTTP request spans/metrics."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:HttpClientInstrumentationEnabled", "HttpClient instrumentation", "Telemetry", settings => settings.Telemetry.HttpClientInstrumentationEnabled, "Instruments outbound HttpClient spans/metrics."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:RuntimeInstrumentationEnabled", "Runtime metrics instrumentation", "Telemetry", settings => settings.Telemetry.RuntimeInstrumentationEnabled, "Emits runtime/system metrics from .NET runtime instrumentation."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:RecordExceptions", "Record exceptions", "Telemetry", settings => settings.Telemetry.RecordExceptions, "Includes exception events/status in OTel spans where supported."),
        EditableSettingDescriptor.Integer("MemorySmith:Telemetry:TraceSamplingPercentage", "Trace sampling percentage", "Telemetry", settings => settings.Telemetry.TraceSamplingPercentage, 0, 100, "Parent-based trace sampling percentage. Lower values reduce overhead in busy environments."),
        EditableSettingDescriptor.Boolean("MemorySmith:Telemetry:ExporterEnabled", "OTLP exporter enabled", "Telemetry", settings => settings.Telemetry.ExporterEnabled, "Enables OTLP export for traces/metrics to a local or remote collector endpoint."),
        EditableSettingDescriptor.String("MemorySmith:Telemetry:OtlpEndpoint", "OTLP endpoint", "Telemetry", settings => settings.Telemetry.OtlpEndpoint, 500, "OTLP collector endpoint URL used when exporter is enabled."),
        EditableSettingDescriptor.Choice("MemorySmith:Telemetry:OtlpProtocol", "OTLP protocol", "Telemetry", settings => settings.Telemetry.OtlpProtocol, ["grpc", "http/protobuf"], "Transport protocol used by OTLP export when sending traces and metrics to a collector endpoint."),
        EditableSettingDescriptor.StringList("MemorySmith:Telemetry:ExcludedRequestPathPrefixes", "Excluded request path prefixes", "Telemetry", settings => settings.Telemetry.ExcludedRequestPathPrefixes, "Request-path prefixes, one per line, excluded from OTel request tracing to reduce noisy/low-value telemetry."),

        EditableSettingDescriptor.String("MemorySmith:History:RootPath", "Version history path", "History", settings => settings.History.RootPath, 500, "Root directory for memory and page history artifacts. Keep under Data/.history so rollback evidence travels with local project data."),
        EditableSettingDescriptor.String("MemorySmith:History:PageMode", "Page history mode", "History", settings => settings.History.PageMode, 100, "Storage mode for markdown page history snapshots. The current app writes page snapshots so users can inspect and restore previous page versions."),
        EditableSettingDescriptor.String("MemorySmith:History:MemoryMode", "Memory history mode", "History", settings => settings.History.MemoryMode, 100, "Storage mode for structured memory history. JsonPatchWithCheckpoints keeps compact diffs with periodic full checkpoints for restore safety."),
        EditableSettingDescriptor.Integer("MemorySmith:History:MemoryCheckpointEveryVersions", "Memory checkpoint interval", "History", settings => settings.History.MemoryCheckpointEveryVersions, 1, 1000, "Number of memory versions between full snapshot checkpoints. Lower values use more disk but make restore chains shorter and easier to audit."),

        EditableSettingDescriptor.Choice("MemorySmith:Pages:DefaultMinimumRole", "Default page visibility", "Pages", settings => PageAccessLevels.Normalize(settings.Pages.DefaultMinimumRole), PageAccessLevels.All, "Default minimum role for newly saved wiki pages when no page-specific visibility is supplied. Anonymous exposes the page publicly; Authenticated/Admin restrict access."),
        EditableSettingDescriptor.Boolean("MemorySmith:Pages:AllowRawHtml", "Allow raw page HTML", "Pages", settings => settings.Pages.AllowRawHtml, "Allows trusted markdown pages to render raw HTML. Keep disabled for safer local wiki rendering unless you fully trust page authors and content."),
        EditableSettingDescriptor.Boolean("MemorySmith:Markdown:MermaidEnabled", "Enable Mermaid diagrams", "Pages", settings => settings.Markdown.MermaidEnabled, "Enables Mermaid diagram rendering in markdown surfaces (chat, pages, memories). Disable to show Mermaid code blocks as plain text only."),
        EditableSettingDescriptor.Choice("MemorySmith:Markdown:MermaidRestrictionMode", "Mermaid restriction mode", "Pages", settings => MermaidRestrictionModes.Normalize(settings.Markdown.MermaidRestrictionMode), MermaidRestrictionModes.All, "Controls Mermaid policy strictness. standard allows broad syntax, restricted blocks risky directives and oversized diagrams, and strict allows only safe diagram families with tighter size limits."),

        EditableSettingDescriptor.Boolean("MemorySmith:SemanticSearch:EmbeddingsEnabled", "Semantic embeddings enabled", "Search", settings => settings.SemanticSearch.EmbeddingsEnabled, "Enables ONNX embedding search when model and vocabulary assets are available. When unavailable, MemorySmith falls back to local token semantic scoring and reports provider metadata."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:ModelPath", "Embedding model path", "Search", settings => settings.SemanticSearch.ModelPath, 500, "Path to the ONNX embedding model used for semantic ranking. Keep paired with the matching vocabulary file to avoid falling back to token scoring."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:VocabularyPath", "Embedding vocabulary path", "Search", settings => settings.SemanticSearch.VocabularyPath, 500, "Path to the vocabulary file used by the ONNX embedding model. Model/vocabulary mismatch can reduce semantic quality or disable embeddings."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:TokenizerKind", "Embedding tokenizer kind", "Search", settings => settings.SemanticSearch.TokenizerKind, 100, "Tokenizer convention used by the ONNX embedding provider. WordPiece is the supported default for the current local provider."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:PoolingMode", "Embedding pooling mode", "Search", settings => settings.SemanticSearch.PoolingMode, 100, "Sequence-output pooling mode for ONNX embeddings. Mean is the default; Cls is available for compatible models."),
        EditableSettingDescriptor.Choice("MemorySmith:SemanticSearch:ExecutionProvider", "Embedding execution provider", "Search", settings => OnnxEmbeddingModelConventions.DisplayExecutionProvider(settings.SemanticSearch.ExecutionProvider), ["Cpu", "Cuda", "OpenVino"], "Preferred ONNX Runtime execution provider. Cpu is the portable default. Cuda requires a CUDA-flavored ONNX Runtime build plus NVIDIA runtime dependencies. OpenVino requires an OpenVINO-flavored build on a supported Windows host."),
        EditableSettingDescriptor.Boolean("MemorySmith:SemanticSearch:PrewarmOnStartupEnabled", "Prewarm embeddings on startup", "Search", settings => settings.SemanticSearch.PrewarmOnStartupEnabled, "When enabled, MemorySmith starts an immediate background prewarm after app startup so the first semantic query or code-search request does not pay the full ONNX session initialization cost."),
        EditableSettingDescriptor.Boolean("MemorySmith:SemanticSearch:CpuFallbackEnabled", "Allow CPU fallback", "Search", settings => settings.SemanticSearch.CpuFallbackEnabled, "When enabled, failed CUDA or OpenVino initialization falls back to CPU embeddings instead of disabling semantic embeddings entirely."),
        EditableSettingDescriptor.Integer("MemorySmith:SemanticSearch:CudaDeviceId", "CUDA device id", "Search", settings => settings.SemanticSearch.CudaDeviceId, 0, 64, "CUDA device id used when the embedding execution provider is set to Cuda."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:OpenVinoDeviceId", "OpenVINO device id", "Search", settings => settings.SemanticSearch.OpenVinoDeviceId, 100, "Optional OpenVINO device identifier. Leave blank to let ONNX Runtime choose the default OpenVINO target."),
        EditableSettingDescriptor.Integer("MemorySmith:SemanticSearch:MaxInputTokens", "Max embedding input tokens", "Search", settings => settings.SemanticSearch.MaxInputTokens, 64, 4096, "Maximum token count sent into the embedding model for one query or document chunk. Increase for richer recall only after measuring memory and latency impact."),
        EditableSettingDescriptor.Integer("MemorySmith:SemanticSearch:MaxIndexedTextCharacters", "Max indexed text characters", "Search", settings => settings.SemanticSearch.MaxIndexedTextCharacters, 500, 50000, "Maximum characters from each memory record considered for semantic indexing/ranking. Higher values improve long-record recall but increase embedding work."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:QueryPrefix", "Semantic query prefix", "Search", settings => settings.SemanticSearch.QueryPrefix, 100, "Prefix prepended to user queries for embedding models that distinguish query and passage text. Keep aligned with the selected embedding model's training convention."),
        EditableSettingDescriptor.String("MemorySmith:SemanticSearch:DocumentPrefix", "Semantic document prefix", "Search", settings => settings.SemanticSearch.DocumentPrefix, 100, "Prefix prepended to memory/page text before embedding. Keep aligned with the embedding model so query and document vectors stay comparable."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:HybridVectorWeight", "Code-search hybrid vector weight", "Search", settings => settings.CodeSearch.HybridVectorWeight, 0, 2, "Weight applied to vector similarity in hybrid ranking. Increase to favor embedding similarity; decrease to favor lexical evidence."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:HybridLexicalWeight", "Code-search hybrid lexical weight", "Search", settings => settings.CodeSearch.HybridLexicalWeight, 0, 2, "Weight applied to normalized lexical evidence in hybrid ranking."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:ZeroLexicalEvidencePenalty", "Code-search zero lexical penalty", "Search", settings => settings.CodeSearch.ZeroLexicalEvidencePenalty, 0, 2, "Multiplier applied when vector candidates have no lexical evidence. Lower values demote semantic-only matches."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:LexicalScoreSaturation", "Code-search lexical saturation", "Search", settings => settings.CodeSearch.LexicalScoreSaturation, 0.001, 100, "Saturation factor for lexical normalization in hybrid ranking. Higher values flatten lexical impact."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:LexicalFrequencyBonusScale", "Code-search lexical frequency scale", "Search", settings => settings.CodeSearch.LexicalFrequencyBonusScale, 0, 5, "Scale factor for repeated-token lexical bonus. Keep modest to avoid keyword-stuffing bias."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:MaxLexicalFrequencyBonusPerToken", "Code-search max lexical frequency bonus", "Search", settings => settings.CodeSearch.MaxLexicalFrequencyBonusPerToken, 0, 10, "Upper bound for per-token lexical repetition bonus."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:MinTokenCoverageWeight", "Code-search min token coverage weight", "Search", settings => settings.CodeSearch.MinTokenCoverageWeight, 0, 3, "Weight applied to low token-coverage matches. Lower values penalize partial-intent matches more aggressively."),
        EditableSettingDescriptor.Decimal("MemorySmith:CodeSearch:MaxTokenCoverageWeight", "Code-search max token coverage weight", "Search", settings => settings.CodeSearch.MaxTokenCoverageWeight, 0, 3, "Weight applied to high token-coverage matches. Higher values boost full-intent matches."),
        EditableSettingDescriptor.Integer("MemorySmith:CodeSearch:VectorPrefilterFullScanFallbackCandidateCount", "Code-search sparse prefilter fallback candidate count", "Search", settings => settings.CodeSearch.VectorPrefilterFullScanFallbackCandidateCount, 0, 10000, "When vector prefilter yields this many or fewer candidates, run a full-index vector fallback pass to protect semantic recall for sparse lexical queries. Set 0 to disable."),
        EditableSettingDescriptor.Integer("MemorySmith:CodeSearch:MaxResults", "Code-search max results", "Search", settings => settings.CodeSearch.MaxResults, 1, 500, "Operator cap for one code-search query. Per-query limits cannot exceed this value."),
        EditableSettingDescriptor.Integer("MemorySmith:CodeSearch:MaxResultsPerDocument", "Code-search max results per document", "Search", settings => settings.CodeSearch.MaxResultsPerDocument, 1, 50, "Maximum number of snippets returned from the same document in one code-search query."),
        EditableSettingDescriptor.Boolean("MemorySmith:TaskSearch:HybridSemanticEnabled", "Task hybrid semantic search", "Search", settings => settings.TaskSearch.HybridSemanticEnabled, "Enables hybrid lexical+semantic ranking for task list queries. Keep enabled for better task recall with reordered or loosely phrased queries."),
        EditableSettingDescriptor.String("MemorySmith:TaskAttachments:StoragePath", "Task attachment storage path", "Tasks", settings => settings.TaskAttachments.StoragePath, 500, "Directory where uploaded task attachment files are stored. Keep under artifacts/task-attachments for portable local artifact cleanup."),
        EditableSettingDescriptor.Integer("MemorySmith:TaskAttachments:MaxFileBytes", "Task attachment max bytes", "Tasks", settings => (int)Math.Min(settings.TaskAttachments.MaxFileBytes, int.MaxValue), 1024, 2147483647, "Maximum size accepted for one uploaded task attachment file."),
        EditableSettingDescriptor.String("MemorySmith:Governance:TagPolicyPath", "Tag policy path", "Governance", settings => settings.Governance.TagPolicyPath, 500, "Path to the JSON tag policy that drives tag namespaces, allow/block lists, aliases, diagnostics, and the Tag Manager editor."),

        EditableSettingDescriptor.Boolean("MemorySmith:Training:ChatTranscriptEnabled", "Chat transcript capture enabled", "Training", settings => settings.Training.ChatTranscriptEnabled, "When enabled, assistant turns are written to transcript JSONL for fine-tuning/export workflows."),
        EditableSettingDescriptor.Boolean("MemorySmith:Training:StoreChatContent", "Store chat transcript content", "Training", settings => settings.Training.StoreChatContent, "When enabled alongside transcript capture, literal user/assistant text is written to companion content files for training export."),
        EditableSettingDescriptor.Integer("MemorySmith:Training:TranscriptRetentionDays", "Transcript retention days", "Training", settings => settings.Training.TranscriptRetentionDays, 1, 3650, "Maximum age in days for transcript files before automatic cleanup removes stale files."),
        EditableSettingDescriptor.Boolean("MemorySmith:Training:TranscriptRedactionEnabled", "Transcript redaction enabled", "Training", settings => settings.Training.TranscriptRedactionEnabled, "Redacts common token, secret, and authorization patterns before transcript content is written."),
        EditableSettingDescriptor.Boolean("MemorySmith:Training:FeedbackEnabled", "Chat feedback capture enabled", "Training", settings => settings.Training.FeedbackEnabled, "Enables server-side thumbs/feedback persistence for training signal collection APIs."),
        EditableSettingDescriptor.Integer("MemorySmith:Training:MaxRunMinutes", "Training run max minutes", "Training", settings => settings.Training.MaxRunMinutes, 1, 1440, "Maximum wall-clock minutes allowed for a local training run before timeout/abort safeguards."),
        EditableSettingDescriptor.Choice("MemorySmith:Training:PreferenceFormat", "Training preference format", "Training", settings => settings.Training.PreferenceFormat.ToString(), ["FilteredSft", "Dpo", "Orpo"], "Preferred export format for model-improvement data. FilteredSft is the default staged format."),
        EditableSettingDescriptor.String("MemorySmith:Training:ActiveModelTag", "Active training model tag", "Training", settings => settings.Training.ActiveModelTag, 200, "Current promoted or preferred model tag used by training workflows when an explicit target is needed."),
        EditableSettingDescriptor.String("MemorySmith:Training:FallbackModelTag", "Fallback training model tag", "Training", settings => settings.Training.FallbackModelTag, 200, "Fallback base model tag used when no promoted fine-tuned training target is configured."),
        EditableSettingDescriptor.String("MemorySmith:Training:TranscriptDirectory", "Transcript directory", "Training", settings => settings.Training.TranscriptDirectory, 500, "Directory for daily chat transcript JSONL files used by fine-tuning export workflows."),
        EditableSettingDescriptor.String("MemorySmith:Training:TrainingDataExportPath", "Training export path", "Training", settings => settings.Training.TrainingDataExportPath, 500, "Directory where curated SFT/DPO/ORPO training exports are written."),
        EditableSettingDescriptor.String("MemorySmith:Training:RunsDirectory", "Training runs directory", "Training", settings => settings.Training.RunsDirectory, 500, "Directory containing per-run training/eval artifacts and status outputs."),
        EditableSettingDescriptor.String("MemorySmith:Training:PythonVenvPath", "Training Python venv path", "Training", settings => settings.Training.PythonVenvPath, 500, "Path to the Python virtual environment used by the training harness bridge."),
        EditableSettingDescriptor.String("MemorySmith:Training:PythonHarnessScript", "Training harness script", "Training", settings => settings.Training.PythonHarnessScript, 500, "Path to the Python harness entrypoint script invoked by .NET orchestration."),

        EditableSettingDescriptor.Boolean("MemorySmith:Maintenance:Enabled", "Maintenance enabled", "Maintenance", settings => settings.Maintenance.Enabled, "Enables background maintenance loops for triage, indexing, consolidation, and related housekeeping. Disable for isolated tests or when manually controlling maintenance jobs."),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:TriageMinutes", "Triage interval minutes", "Maintenance", settings => settings.Maintenance.TriageMinutes, 1, 1440, "How often the maintenance host evaluates records for status transitions, diagnostics, and warning-first deprecation recommendations."),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:IndexingMinutes", "Indexing interval minutes", "Maintenance", settings => settings.Maintenance.IndexingMinutes, 1, 1440, "How often the maintenance host rebuilds or refreshes search/index state from the current memory store snapshot."),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:ConsolidationHours", "Consolidation interval hours", "Maintenance", settings => settings.Maintenance.ConsolidationHours, 1, 720, "How often duplicate/related-record consolidation checks are allowed to run. Keep conservative to avoid noisy maintenance churn."),
        EditableSettingDescriptor.Integer("MemorySmith:Maintenance:StartupGraceSeconds", "Startup grace seconds", "Maintenance", settings => settings.Maintenance.StartupGraceSeconds, 0, 3600, "Delay after app startup before background maintenance begins. Useful for allowing the UI/database to settle after service restart."),
        EditableSettingDescriptor.Boolean("MemorySmith:Maintenance:AutomaticDeprecationEnabled", "Automatic deprecation enabled", "Maintenance", settings => settings.Maintenance.AutomaticDeprecationEnabled, "Allows low-score records to move to Deprecated automatically. The project default is warning-first so weak records stay visible until a person or explicit workflow decides."),

        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxPageSize", "Max page size", "Limits", settings => settings.Limits.MaxPageSize, 1, 1000, "Maximum page size accepted by memory list APIs. This protects UI/API calls from accidentally requesting huge result pages."),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxSearchLimit", "Max search limit", "Limits", settings => settings.Limits.MaxSearchLimit, 1, 1000, "Maximum search result count accepted by lexical, semantic, hybrid, and related memory query endpoints."),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxContentLength", "Max memory content length", "Limits", settings => settings.Limits.MaxContentLength, 1000, 250000, "Maximum characters allowed in a structured memory record's content field before validation rejects the save."),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxTags", "Max tags per memory", "Limits", settings => settings.Limits.MaxTags, 1, 500, "Maximum number of tags allowed on one memory record. Keep bounded so tag governance and search filtering remain understandable."),
        EditableSettingDescriptor.Integer("MemorySmith:Limits:MaxReferences", "Max references per memory", "Limits", settings => settings.Limits.MaxReferences, 1, 2000, "Maximum reference/conflict relationship count allowed on one memory record. Protects context-pack graph expansion and relationship diagnostics from runaway records."),

        EditableSettingDescriptor.Boolean("MemorySmith:SourceLinks:AllowOpenWithDefaultApp", "Open source links with OS", "Source links", settings => settings.SourceLinks.AllowOpenWithDefaultApp, "Allows approved local file source links to open through the operating system default app after source-root checks. Keep disabled when you only want copy/read behavior."),
        EditableSettingDescriptor.Integer("MemorySmith:SourceLinks:MaxReadBytes", "Max source read bytes", "Source links", settings => settings.SourceLinks.MaxReadBytes, 1024, 1048576, "Maximum bytes read from a source-linked file for source bundle/tool output. Prevents large local files from flooding chat, MCP, or diagnostics responses."),
        EditableSettingDescriptor.Boolean("MemorySmith:SourceLinks:AllowUnrestrictedSourceReads", "Allow unrestricted source reads", "Source links", settings => settings.SourceLinks.AllowUnrestrictedSourceReads, "Lets source-linked reads access any local file path unless it is explicitly denied. Keep this off unless you need broad read access in a trusted environment."),
        EditableSettingDescriptor.Integer("MemorySmith:SourceLinks:ReadContextLinesBefore", "Source read context before", "Source links", settings => settings.SourceLinks.ReadContextLinesBefore, 0, 200, "How many lines of leading context to include before the requested source line range."),
        EditableSettingDescriptor.Integer("MemorySmith:SourceLinks:ReadContextLinesAfter", "Source read context after", "Source links", settings => settings.SourceLinks.ReadContextLinesAfter, 0, 200, "How many lines of trailing context to include after the requested source line range."),
        EditableSettingDescriptor.StringList("MemorySmith:SourceLinks:AllowedFileRootVariables", "Allowed source root variables", "Source links", settings => settings.SourceLinks.AllowedFileRootVariables, "Variable names, one per line, whose resolved paths are allowed as local file source roots. Source links outside these roots are blocked."),
        EditableSettingDescriptor.StringList("MemorySmith:SourceLinks:AllowedFileRoots", "Allowed source root paths", "Source links", settings => settings.SourceLinks.AllowedFileRoots, "Absolute filesystem roots, one per line, that source-link reads/open operations may access in addition to allowed variable roots. Keep this list narrow."),
        EditableSettingDescriptor.StringList("MemorySmith:SourceLinks:DeniedFileRootVariables", "Denied source root variables", "Source links", settings => settings.SourceLinks.DeniedFileRootVariables, "Variable names, one per line, whose resolved paths are always blocked for source reads/open operations."),
        EditableSettingDescriptor.StringList("MemorySmith:SourceLinks:DeniedFileRoots", "Denied source root paths", "Source links", settings => settings.SourceLinks.DeniedFileRoots, "Absolute filesystem roots, one per line, that are always blocked for source reads/open operations even if a broader read mode is enabled."),

        EditableSettingDescriptor.StringList("MemorySmith:Mcp:EnabledTools", "Enabled MCP tools", "MCP", settings => settings.Mcp.EnabledTools, "MCP tool names, one per line, to opt specific sensitive-read or write tools into the external MCP surface. Safe read tools stay on by default; disabled tools still take precedence over this list."),
        EditableSettingDescriptor.StringList("MemorySmith:Mcp:DisabledTools", "Disabled MCP tools", "MCP", settings => settings.Mcp.DisabledTools, "MCP tool names, one per line, to hide from tools/list and reject during tools/call. Use this to lock down even normally safe read tools for stricter deployments or narrower automation profiles."),
        EditableSettingDescriptor.Integer("MemorySmith:Mcp:MaxToolResponseCharacters", "Max MCP tool response characters", "MCP", settings => settings.Mcp.MaxToolResponseCharacters, 256, 200000, "Maximum characters returned in one MCP tool response payload before MemorySmith truncates the text and emits deterministic truncation metadata. Keep this bounded so external MCP clients can detect incomplete payloads and retry with narrower reads."),

        EditableSettingDescriptor.Choice("MemorySmith:Chat:Provider", "Default chat provider", "Chat", settings => settings.Chat.Provider, ["Ollama", "GitHubCopilot"], "Default provider selected for MemorySmith chat when a request does not explicitly choose a provider. Provider capability metadata is shown in chat config."),
        EditableSettingDescriptor.String("MemorySmith:Chat:OllamaEndpoint", "Ollama endpoint", "Chat", settings => settings.Chat.OllamaEndpoint, 200, "Base URL for the local Ollama chat API. The app uses it for model listing, completion, streaming, image input, and usage estimates when Ollama is selected."),
        EditableSettingDescriptor.String("MemorySmith:Chat:OllamaModel", "Ollama model", "Chat", settings => settings.Chat.OllamaModel, 100, "Default Ollama model name used for chat requests when the user does not pick a specific model."),
        EditableSettingDescriptor.NullableInteger("MemorySmith:Chat:OllamaContextWindowTokens", "Ollama context window tokens", "Chat", settings => settings.Chat.OllamaContextWindowTokens, 512, 262144, "Optional context-window size reported for the configured Ollama model. Leave blank when the provider should report or estimate context usage itself."),
        EditableSettingDescriptor.String("MemorySmith:Chat:GitHubModel", "GitHub model", "Chat", settings => settings.Chat.GitHubModel, 100, "Default GitHub Copilot model id used when the GitHub provider is selected and the user has not selected another model."),
        EditableSettingDescriptor.String("MemorySmith:Chat:GitHubCliPath", "GitHub CLI path", "Chat", settings => settings.Chat.GitHubCliPath, 500, "Optional explicit path to the GitHub CLI used for Copilot authentication fallback. Leave blank to use normal PATH discovery."),
        EditableSettingDescriptor.String("MemorySmith:Chat:GitHubCliUrl", "GitHub CLI URL", "Chat", settings => settings.Chat.GitHubCliUrl, 500, "Optional URL used to guide GitHub CLI installation or authentication troubleshooting in chat provider errors."),
        EditableSettingDescriptor.String("MemorySmith:Chat:GitHubTokenEnvironmentVariable", "GitHub token env var", "Chat", settings => settings.Chat.GitHubTokenEnvironmentVariable, 100, "Environment variable name the GitHub provider checks for a token. This is the variable name only, not the token value."),
        EditableSettingDescriptor.String("MemorySmith:Chat:SystemPromptPath", "System prompt path", "Chat", settings => settings.Chat.SystemPromptPath, 500, "Path to the wiki chat/agent system prompt loaded by MemoryChatAgent. The prompt defines local tool protocol, output formatting, and safety boundaries."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:RequestTimeoutSeconds", "Chat request timeout seconds", "Chat", settings => settings.Chat.RequestTimeoutSeconds, 10, 3600, "Maximum wall-clock time allowed for one provider chat request before the app cancels it and reports an error."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxContextRecords", "Max memory context records", "Chat", settings => settings.Chat.MaxContextRecords, 0, 100, "Maximum memory records considered for chat context/tool retrieval. The context planner may choose a smaller preload based on user intent."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxContextPages", "Max page context results", "Chat", settings => settings.Chat.MaxContextPages, 0, 100, "Maximum markdown pages considered for chat context/tool retrieval. The context planner may choose page-only, memory-only, mixed, or no preload."),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:PreloadContextEnabled", "Preload chat context", "Chat", settings => settings.Chat.PreloadContextEnabled, "Allows the context planner to preload MemorySmith memories/pages before the model call when the prompt shows local wiki evidence intent."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxPreloadedContextRecords", "Max preloaded memories", "Chat", settings => settings.Chat.MaxPreloadedContextRecords, 0, 25, "Upper bound for memory records sent before the first model response. Keeps routine chat turns from carrying unnecessary wiki context."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxPreloadedContextPages", "Max preloaded pages", "Chat", settings => settings.Chat.MaxPreloadedContextPages, 0, 25, "Upper bound for markdown pages sent before the first model response. Page-heavy prompts can still use page search tools for more evidence."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxContextItemCharacters", "Max context item characters", "Chat", settings => settings.Chat.MaxContextItemCharacters, 500, 50000, "Maximum characters included from one memory/page context item in a chat turn. Protects token budget while preserving source identity."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxHistoryMessages", "Max history messages", "Chat", settings => settings.Chat.MaxHistoryMessages, 0, 200, "Maximum prior chat messages carried into a provider request. Lower values reduce token use; higher values preserve more conversational continuity."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxAttachmentCharacters", "Max attachment characters", "Chat", settings => settings.Chat.MaxAttachmentCharacters, 0, 500000, "Maximum extracted text characters accepted from user attachments for one chat request before truncation."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxAttachmentBytes", "Max attachment bytes", "Chat", settings => (int)Math.Min(settings.Chat.MaxAttachmentBytes, int.MaxValue), 0, 2147483647, "Maximum uploaded attachment size in bytes for chat. Image attachments may also require provider/model vision support."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:AttachmentTempFileRetentionHours", "Attachment temp retention hours", "Chat", settings => settings.Chat.AttachmentTempFileRetentionHours, 1, 720, "Maximum age for chat image attachment temp files before the Chat route cleans them up. Cleanup logs counts only, not local file paths."),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:ClipboardFetchExternalImagesEnabled", "Fetch external clipboard image URLs", "Chat", settings => settings.Chat.ClipboardFetchExternalImagesEnabled, "When enabled, clipboard HTML/plain-text image URLs may be fetched over HTTP/HTTPS and attached as images. Keep disabled to avoid unprompted network fetches during paste."),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:ToolCallsEnabled", "Tool calls enabled", "Chat", settings => settings.Chat.ToolCallsEnabled, "Allows the app-intercepted MemorySmith tool-call protocol in chat/agent responses. Chat mode exposes read-only memory/page/task lookup tools; Agent mode can additionally use gated mutation tools."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxToolIterations", "Max tool iterations", "Chat", settings => settings.Chat.MaxToolIterations, 0, 10, "Maximum follow-up model/tool result loops allowed after a provider requests MemorySmith wiki tools. Bounds recursive tool use."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxToolCallsPerTurn", "Max tool calls per turn", "Chat", settings => settings.Chat.MaxToolCallsPerTurn, 0, 20, "Maximum MemorySmith wiki tool calls accepted from one model response. Prevents one turn from running too many local searches or gets."),
        EditableSettingDescriptor.Integer("MemorySmith:Chat:MaxToolResultCharacters", "Max tool result characters", "Chat", settings => settings.Chat.MaxToolResultCharacters, 1000, 100000, "Maximum characters returned from one intercepted tool result to the model. Keeps tool output bounded and traceable."),
        EditableSettingDescriptor.Boolean("MemorySmith:Chat:AgentWritesEnabled", "Agent writes enabled", "Chat", settings => settings.Chat.AgentWritesEnabled, "Allows Agent mode memory/page write proposals and Agent-only task mutation tools when user role and approval flow permit them. Chat mode remains read-only and cannot create memories/pages/tasks."),
        EditableSettingDescriptor.String("MemorySmith:Chat:AgentWriteApprovalMode", "Agent write approval mode", "Chat", settings => settings.Chat.AgentWriteApprovalMode, 100, "Controls Agent write application. Manual keeps mutation tools unavailable while memory/page writes wait for review; auto_accept allows trusted task mutation tools while memory/page writes still enter the proposal workflow."),
        EditableSettingDescriptor.StringList("MemorySmith:Chat:AgentWriteRoots", "Chat proposal write roots", "Chat", settings => settings.Chat.AgentWriteRoots, "Paths chat-agent memory/page proposals may target after approval, one per line. Defaults to Data/Memories/Working and Data/Pages and is intentionally separate from MaintenanceAgent:Write."),

        EditableSettingDescriptor.StringList("MemorySmith:MaintenanceAgent:Read", "Agent read roots", "Maintenance agent", settings => settings.MaintenanceAgent.Read, "Paths the maintenance agent may read, one per line. Defaults include Data/Memories and Data/Pages so reviews stay grounded in the project wiki."),
        EditableSettingDescriptor.StringList("MemorySmith:MaintenanceAgent:Write", "Agent write roots", "Maintenance agent", settings => settings.MaintenanceAgent.Write, "Paths the maintenance agent may write, one per line. Keep narrow; current defaults route memory writes to Working and allow wiki page updates."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:UseLlm", "Use LLM review", "Maintenance agent", settings => settings.MaintenanceAgent.UseLlm, "Enables LLM-assisted maintenance review for configured tasks. Disable for deterministic/manual-only maintenance runs."),
        EditableSettingDescriptor.Choice("MemorySmith:MaintenanceAgent:Provider", "Agent provider", "Maintenance agent", settings => settings.MaintenanceAgent.Provider, ["Ollama", "GitHub"], "Provider used by maintenance-agent LLM review tasks. Keep aligned with locally available credentials and model capacity."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:OllamaEndpoint", "Agent Ollama endpoint", "Maintenance agent", settings => settings.MaintenanceAgent.OllamaEndpoint, 200, "Ollama endpoint used by maintenance-agent tasks when the agent provider is Ollama."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Model", "Agent model", "Maintenance agent", settings => settings.MaintenanceAgent.Model, 100, "Model id used by the maintenance agent for review tasks. Choose a model that can handle wiki diagnostics and concise proposals."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:ModelProfileId", "Maintenance model profile", "Maintenance agent", settings => settings.MaintenanceAgent.ModelProfileId ?? string.Empty, 100, "Optional Admin Models profile id for maintenance runs. When set, it overrides the legacy provider/model fields for scheduled and manual maintenance tasks."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:ProposalReviewModelProfileId", "Proposal review model profile", "Maintenance agent", settings => settings.MaintenanceAgent.ProposalReviewModelProfileId ?? string.Empty, 100, "Optional Admin Models profile id for Request Agent Review. Leave blank to inherit the maintenance model profile or legacy maintenance provider/model settings."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:AdminChatModelProfileId", "Admin chat model profile", "Maintenance agent", settings => settings.MaintenanceAgent.AdminChatModelProfileId ?? string.Empty, 100, "Optional Admin Models profile id for the non-mutating Admin Maintenance chat. Leave blank to inherit the maintenance model profile or legacy maintenance provider/model settings."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:AgentVersion", "Agent prompt version", "Maintenance agent", settings => settings.MaintenanceAgent.AgentVersion, 100, "Version label embedded in maintenance-agent prompts/results so future audits know which instruction contract produced findings."),
        EditableSettingDescriptor.Integer("MemorySmith:MaintenanceAgent:MaxFindingsPerTask", "Max findings per task", "Maintenance agent", settings => settings.MaintenanceAgent.MaxFindingsPerTask, 1, 500, "Maximum number of findings one maintenance task should emit. Keeps review output manageable and prevents one task from flooding the workbench."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:DirectWrite", "Allow direct agent writes", "Maintenance agent", settings => settings.MaintenanceAgent.DirectWrite, "Allows the maintenance agent to write directly inside its configured write roots. Keep false unless the task is low-risk and the write roots are intentionally constrained."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ActionUx:ShowAccept", "Show accept action", "Maintenance agent", settings => settings.MaintenanceAgent.ActionUx.ShowAccept, "Shows the Accept action on /proposals. Accept still applies the underlying diff when the proposal is actionable."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ActionUx:ShowRespond", "Show respond action", "Maintenance agent", settings => settings.MaintenanceAgent.ActionUx.ShowRespond, "Shows the Respond action on /proposals so reviewers can send proposals back for revision with a human comment."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ActionUx:ShowReject", "Show reject action", "Maintenance agent", settings => settings.MaintenanceAgent.ActionUx.ShowReject, "Shows the Reject action on /proposals so reviewers can explicitly decline a proposal without applying it."),
        EditableSettingDescriptor.Choice("MemorySmith:MaintenanceAgent:ActionUx:DefaultAction", "Proposal default action", "Maintenance agent", settings => MaintenanceProposalActionUx.NormalizeDefaultAction(settings.MaintenanceAgent.ActionUx), MaintenanceProposalActionUx.All, "Primary proposal action on /proposals. Accept applies the diff, Respond requests revision, and Reject closes the proposal without applying it."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ActionUx:RevisionRequired", "Revision required before accept", "Maintenance agent", settings => settings.MaintenanceAgent.ActionUx.RevisionRequired, "When true, proposals marked NeedsRevision must be superseded by a revised proposal before they can be accepted. When false, reviewers may accept the same proposal after a respond cycle."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:spot_checks", "Enable spot checks", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "spot_checks"), "Runs general spot-check reviews over wiki content to find obvious stale, sparse, or inconsistent entries."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:staleness_scan", "Enable staleness scan", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "staleness_scan"), "Runs review-after/expires/stale-risk checks so old knowledge can be surfaced without silently removing it."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:consistency_checks", "Enable consistency checks", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "consistency_checks"), "Looks for contradictions, weak evidence, and cross-entry inconsistencies in current wiki memories/pages."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:relationship_integrity", "Enable relationship integrity checks", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "relationship_integrity"), "Checks references, conflicts, supersedes, and superseded-by relationships for missing or inconsistent targets."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:topic_map", "Enable topic map maintenance", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "topic_map"), "Allows the agent to refresh topic-map style summaries/caches from the current wiki corpus."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:synthesis", "Enable synthesis maintenance", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "synthesis"), "Allows the agent to propose synthesized current-state knowledge from multiple records. Keep disabled unless review capacity is available."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Tasks:embedding_chunking_maintenance", "Enable embedding chunking maintenance", "Maintenance agent", settings => MaintenanceTaskEnabled(settings, "embedding_chunking_maintenance"), "Runs maintenance checks related to embedding/chunking readiness without changing ranking or page chunking behavior by itself."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Schedule:Enabled", "Weekly scheduler enabled", "Maintenance agent", settings => settings.MaintenanceAgent.Schedule.Enabled, "Enables scheduled weekly maintenance-agent runs. Manual runs and background maintenance are separate from this weekly LLM review schedule."),
        EditableSettingDescriptor.Choice("MemorySmith:MaintenanceAgent:Schedule:WeeklyDay", "Weekly run day", "Maintenance agent", settings => settings.MaintenanceAgent.Schedule.WeeklyDay, DayOfWeekOptions, "Local weekday for scheduled maintenance-agent runs when the weekly scheduler is enabled."),
        EditableSettingDescriptor.Integer("MemorySmith:MaintenanceAgent:Schedule:WeeklyHourLocal", "Weekly run hour", "Maintenance agent", settings => settings.MaintenanceAgent.Schedule.WeeklyHourLocal, 0, 23, "Local hour, 0-23, for weekly maintenance-agent runs. Choose quiet hours when local model and disk activity are acceptable."),
        EditableSettingDescriptor.Integer("MemorySmith:MaintenanceAgent:Schedule:MinimumHoursBetweenRuns", "Minimum hours between runs", "Maintenance agent", settings => settings.MaintenanceAgent.Schedule.MinimumHoursBetweenRuns, 1, 720, "Minimum spacing between scheduled maintenance-agent runs so restarts or clock changes do not trigger repeated reviews."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ResourceProbe:Enabled", "Resource probe enabled", "Maintenance agent", settings => settings.MaintenanceAgent.ResourceProbe.Enabled, "Checks local activity before optional LLM maintenance work. Useful on a workstation where model tasks should avoid busy periods."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:ResourceProbe:SkipWhenBusy", "Skip when busy", "Maintenance agent", settings => settings.MaintenanceAgent.ResourceProbe.SkipWhenBusy, "Skips optional maintenance-agent runs when busy-process probes indicate the machine is in active use."),
        EditableSettingDescriptor.StringList("MemorySmith:MaintenanceAgent:ResourceProbe:BusyProcessNames", "Busy process names", "Maintenance agent", settings => settings.MaintenanceAgent.ResourceProbe.BusyProcessNames, "Process names, one per line, that mark the workstation as busy for maintenance-agent scheduling decisions."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Storage:ProposalsPath", "Agent proposals path", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.ProposalsPath, 500, "Directory where maintenance-agent proposal artifacts are stored for review rather than silently changing wiki state."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Storage:TopicMapCachePath", "Topic map cache path", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.TopicMapCachePath, 500, "File path for cached topic-map output produced by maintenance tooling. Cache files are derived state, not the authoritative memory source."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Storage:LastRunPath", "Last run state path", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.LastRunPath, 500, "File path recording the maintenance-agent last-run state used by scheduling and operational visibility."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Storage:ActivityLogPath", "Activity log path", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.ActivityLogPath, 500, "Append-only JSONL file storing recent maintenance-agent run summaries for the Proposals activity panel and operational review."),
        EditableSettingDescriptor.String("MemorySmith:MaintenanceAgent:Storage:TranscriptLogPath", "Transcript log path", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.TranscriptLogPath, 500, "Append-only JSONL file storing admin maintenance-agent conversation turns. This transcript is operational history and does not grant the agent write authority."),
        EditableSettingDescriptor.Integer("MemorySmith:MaintenanceAgent:Storage:TranscriptRetentionEntries", "Transcript retention entries", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.TranscriptRetentionEntries, 1, 10000, "Maximum number of Admin Maintenance transcript entries retained in the JSONL log. Older entries are trimmed after new entries are appended."),
        EditableSettingDescriptor.Boolean("MemorySmith:MaintenanceAgent:Storage:TranscriptRedactionEnabled", "Transcript redaction enabled", "Maintenance agent", settings => settings.MaintenanceAgent.Storage.TranscriptRedactionEnabled, "Redacts common token, API key, secret, authorization, and password patterns before Admin Maintenance transcript entries are persisted.")
    ];

    private static bool MaintenanceTaskEnabled(MemorySmithOptions settings, string taskKey) =>
        settings.MaintenanceAgent.Tasks.TryGetValue(taskKey, out var enabled) && enabled;

    private sealed record EditableSettingDescriptor(
        string Key,
        string Label,
        string Category,
        string HelpText,
        AdminSettingValueKind ValueKind,
        Func<MemorySmithOptions, object?> GetValue,
        IReadOnlyList<string> Options,
        int? Min,
        int? Max,
        double? MinDecimal,
        double? MaxDecimal,
        int? MaxLength,
        bool IsSensitive)
    {
        public AdminSettingItem ToItem(MemorySmithOptions settings)
        {
            var value = GetValue(settings);
            var text = value is IReadOnlyList<string> strings
                ? string.Join(Environment.NewLine, strings)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            var displayValue = IsSensitive
                ? string.IsNullOrWhiteSpace(text) ? "Not configured" : "Configured"
                : text;
            var editValue = IsSensitive ? string.Empty : text;
            return new AdminSettingItem(Key, Label, Category, ValueKind.ToString(), editValue, displayValue, Options, Min, Max, MaxLength, HelpText, IsSensitive);
        }

        public bool TryConvert(string? rawValue, out object value, out string? error)
        {
            value = string.Empty;
            error = null;
            rawValue ??= string.Empty;

            switch (ValueKind)
            {
                case AdminSettingValueKind.Boolean:
                    if (bool.TryParse(rawValue, out var boolean))
                    {
                        value = boolean;
                        return true;
                    }

                    error = "Use true or false.";
                    return false;
                case AdminSettingValueKind.Integer:
                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        error = "Use a whole number.";
                        return false;
                    }

                    if (Min.HasValue && integer < Min.Value || Max.HasValue && integer > Max.Value)
                    {
                        error = $"Use a value between {Min} and {Max}.";
                        return false;
                    }

                    value = integer;
                    return true;
                case AdminSettingValueKind.Decimal:
                    if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        error = "Use a decimal number.";
                        return false;
                    }

                    if (MinDecimal.HasValue && number < MinDecimal.Value || MaxDecimal.HasValue && number > MaxDecimal.Value)
                    {
                        error = $"Use a value between {MinDecimal?.ToString(CultureInfo.InvariantCulture)} and {MaxDecimal?.ToString(CultureInfo.InvariantCulture)}.";
                        return false;
                    }

                    value = number;
                    return true;
                case AdminSettingValueKind.NullableInteger:
                    if (string.IsNullOrWhiteSpace(rawValue))
                    {
                        value = null!;
                        return true;
                    }

                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nullableInteger))
                    {
                        error = "Use a whole number or leave blank to clear the setting.";
                        return false;
                    }

                    if (Min.HasValue && nullableInteger < Min.Value || Max.HasValue && nullableInteger > Max.Value)
                    {
                        error = $"Use a value between {Min} and {Max}, or leave blank to clear the setting.";
                        return false;
                    }

                    value = nullableInteger;
                    return true;
                case AdminSettingValueKind.StringList:
                    value = rawValue
                        .Replace("\r", string.Empty)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToList();
                    return true;
                case AdminSettingValueKind.Choice:
                    var choice = Options.FirstOrDefault(option => string.Equals(option, rawValue, StringComparison.OrdinalIgnoreCase));
                    if (choice is null)
                    {
                        error = "Choose one of the allowed values.";
                        return false;
                    }

                    value = choice;
                    return true;
                default:
                    var text = rawValue.Trim();
                    if (MaxLength.HasValue && text.Length > MaxLength.Value)
                    {
                        error = $"Use {MaxLength.Value} characters or fewer.";
                        return false;
                    }

                    value = text;
                    return true;
            }
        }

        public static EditableSettingDescriptor Boolean(string key, string label, string category, Func<MemorySmithOptions, bool> getValue, string? helpText = null) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.Boolean, settings => getValue(settings), [], null, null, null, null, null, false);

        public static EditableSettingDescriptor Integer(string key, string label, string category, Func<MemorySmithOptions, int> getValue, int min, int max, string? helpText = null) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.Integer, settings => getValue(settings), [], min, max, null, null, null, false);

        public static EditableSettingDescriptor Decimal(string key, string label, string category, Func<MemorySmithOptions, double> getValue, double min, double max, string? helpText = null) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.Decimal, settings => getValue(settings), [], null, null, min, max, null, false);

        public static EditableSettingDescriptor NullableInteger(string key, string label, string category, Func<MemorySmithOptions, int?> getValue, int min, int max, string? helpText = null) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.NullableInteger, settings => getValue(settings), [], min, max, null, null, null, false);

        public static EditableSettingDescriptor Choice(string key, string label, string category, Func<MemorySmithOptions, string> getValue, IReadOnlyList<string> options, string? helpText = null) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.Choice, settings => getValue(settings), options, null, null, null, null, null, false);

        public static EditableSettingDescriptor String(string key, string label, string category, Func<MemorySmithOptions, string?> getValue, int maxLength, string? helpText = null, bool isSensitive = false) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.String, settings => getValue(settings), [], null, null, null, null, maxLength, isSensitive);

        public static EditableSettingDescriptor StringList(string key, string label, string category, Func<MemorySmithOptions, IReadOnlyList<string>> getValue, string? helpText = null) =>
            new(key, label, category, helpText ?? DefaultHelpText(key), AdminSettingValueKind.StringList, settings => getValue(settings), [], null, null, null, null, null, false);

        private static string DefaultHelpText(string key) =>
            $"Controls {key}. Changes are written to the local MemorySmith settings override file and take effect after configuration reload when the owning service reads updated options; startup-only settings may still require an app restart.";
    }
}

public enum AdminSettingValueKind
{
    Boolean,
    Integer,
    Decimal,
    NullableInteger,
    String,
    StringList,
    Choice
}

public sealed record AdminSettingItem(
    string Key,
    string Label,
    string Category,
    string ValueKind,
    string Value,
    string DisplayValue,
    IReadOnlyList<string> Options,
    int? Min,
    int? Max,
    int? MaxLength,
    string HelpText,
    bool IsSensitive);

public sealed record AdminSettingUpdateRequest(string Key, string? Value);

public sealed record AdminSettingUpdateResult(bool Succeeded, string? Error);