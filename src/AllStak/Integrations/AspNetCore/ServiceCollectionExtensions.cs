using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AllStak.Integrations.AspNetCore;

public static class AllStakServiceCollectionExtensions
{
    /// <summary>
    /// Register the AllStak SDK in DI. The client is initialized as a singleton.
    /// Call <c>app.UseAllStak()</c> to register the request middleware.
    /// </summary>
    public static IServiceCollection AddAllStak(this IServiceCollection services, Action<AllStakOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton(sp =>
        {
            var opts = new AllStakOptions();
            configure(opts);
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("allstak.sdk");
            return AllStakClient.InitializeFromOptions(opts, logger);
        });
        return services;
    }
}

public static class AllStakApplicationBuilderExtensions
{
    /// <summary>
    /// Register the AllStak request middleware.
    /// Should be registered BEFORE UseRouting / UseEndpoints and AFTER UseExceptionHandler
    /// (so it can observe the final status code).
    /// </summary>
    public static IApplicationBuilder UseAllStak(this IApplicationBuilder app)
    {
        // Force resolve so the singleton initializes
        _ = app.ApplicationServices.GetService(typeof(AllStakClient));
        return app.UseMiddleware<AllStakMiddleware>();
    }
}
