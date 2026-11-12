using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace SporAkademi.Api;

internal static class Logging
{
    /// <summary>
    /// JSON logs to stdout; Seq when <c>Seq:ServerUrl</c> is configured (local only, NFR-OBS-01).
    /// Log messages carry ids and codes only, never personal data (NFR-PRV-03).
    /// </summary>
    public static WebApplicationBuilder AddStructuredLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                // Sinks registered as ILogEventSink (e.g. the log-privacy test's collector) receive every event too.
                .ReadFrom.Services(services)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                // Last enricher: P3-08's hub token in a query string never reaches a sink (DEC-399).
                .Enrich.With<Observability.AccessTokenRedaction>()
                .WriteTo.Console(new RenderedCompactJsonFormatter());

            var seqUrl = builder.Configuration["Seq:ServerUrl"];
            if (!string.IsNullOrWhiteSpace(seqUrl))
            {
                configuration.WriteTo.Seq(seqUrl, formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            }
        });

        return builder;
    }
}
