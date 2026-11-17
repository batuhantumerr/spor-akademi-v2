using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace SporAkademi.Notifications.Observability;

internal static class WorkerLogging
{
    /// <summary>
    /// JSON logs to stdout; Seq when <c>Seq:ServerUrl</c> is configured (local only, NFR-OBS-01), as in the API. Log messages
    /// carry ids and codes only, never personal data (NFR-PRV-03); the handlers are the same code as in the API.
    /// </summary>
    public static WebApplicationBuilder AddWorkerLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
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
