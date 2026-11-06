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
