namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Reflection;

/// <summary>
/// OpenTelemetry tracing and metrics, driven by MemorySmith:Telemetry options. Extracted from
/// Program.cs (TSK-0282) — the whole block was among those the June 4 reconstruction silently
/// dropped.
/// </summary>
public static class MemorySmithTelemetrySetup
{
    public static WebApplicationBuilder AddMemorySmithTelemetry(this WebApplicationBuilder builder)
    {
        var telemetryOptions = builder.Configuration.GetSection("MemorySmith:Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (!telemetryOptions.Enabled)
        {
            return builder;
        }

        var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var openTelemetry = builder.Services.AddOpenTelemetry().ConfigureResource(resource =>
        {
            resource.Clear();
            resource.AddService(
                serviceName: string.IsNullOrWhiteSpace(telemetryOptions.ServiceName) ? "MemorySmith.App" : telemetryOptions.ServiceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName);
        });

        if (telemetryOptions.TracingEnabled)
        {
            openTelemetry.WithTracing(tracing =>
            {
                tracing
                    .AddSource(MemorySmithTelemetry.ActivitySourceName)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(Math.Clamp(telemetryOptions.TraceSamplingPercentage, 0, 100) / 100d)));

                if (telemetryOptions.AspNetCoreInstrumentationEnabled)
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = telemetryOptions.RecordExceptions;
                        options.Filter = context => !IsTelemetryPathExcluded(context.Request.Path, telemetryOptions.ExcludedRequestPathPrefixes);
                    });
                }

                if (telemetryOptions.HttpClientInstrumentationEnabled)
                {
                    tracing.AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = telemetryOptions.RecordExceptions;
                    });
                }

                if (telemetryOptions.ExporterEnabled)
                {
                    tracing.AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions));
                }
            });
        }

        if (telemetryOptions.MetricsEnabled)
        {
            openTelemetry.WithMetrics(metrics =>
            {
                metrics.AddMeter(MemorySmithTelemetry.MeterName);

                if (telemetryOptions.AspNetCoreInstrumentationEnabled)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (telemetryOptions.HttpClientInstrumentationEnabled)
                {
                    metrics.AddHttpClientInstrumentation();
                }

                if (telemetryOptions.RuntimeInstrumentationEnabled)
                {
                    metrics.AddRuntimeInstrumentation();
                }

                if (telemetryOptions.ExporterEnabled)
                {
                    metrics.AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions));
                }
            });
        }

        return builder;
    }

    internal static bool IsTelemetryPathExcluded(PathString requestPath, IEnumerable<string> excludedPrefixes)
    {
        if (!requestPath.HasValue)
        {
            return false;
        }

        var value = requestPath.Value ?? string.Empty;
        return excludedPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static void ConfigureOtlpExporter(OtlpExporterOptions options, TelemetryOptions telemetryOptions)
    {
        if (Uri.TryCreate(telemetryOptions.OtlpEndpoint, UriKind.Absolute, out var endpoint))
        {
            options.Endpoint = endpoint;
        }

        options.Protocol = telemetryOptions.OtlpProtocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }
}
