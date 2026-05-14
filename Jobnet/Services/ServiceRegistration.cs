using System;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services;

internal static class ServiceRegistration
{
    private static bool _dapperConfigured;

    public static IServiceCollection AddJobnetCore(this IServiceCollection services)
    {
        ConfigureDapper();

        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IDbConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<MigrationRunner>();

        services.AddSingleton<IConfigRepository, ConfigRepository>();
        services.AddSingleton<IAggregatorRepository, AggregatorRepository>();
        services.AddSingleton<ICompanyRepository, CompanyRepository>();
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<ILevelRepository, LevelRepository>();
        services.AddSingleton<IAreaRepository, AreaRepository>();

        services.AddSingleton<Classification.HeuristicClassifier>();
        services.AddSingleton<Classification.ClaudeHaikuClassifier>();
        services.AddSingleton<Classification.IJobClassifier, Classification.CompositeClassifier>();

        services.AddSingleton<ApiUsage.IApiUsageTracker, ApiUsage.ApiUsageTracker>();
        services.AddSingleton<RateLimit.IRateLimiter, RateLimit.RateLimiter>();

        services.AddHttpClient<Discovery.GoogleCseClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.4 (+https://github.com/jobnet)");
        });
        services.AddHttpClient<Discovery.BraveSearchClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.4 (+https://github.com/jobnet)");
        });
        services.AddSingleton<Discovery.ISearchClient, Discovery.RoutingSearchClient>();
        services.AddSingleton<Discovery.IDiscoveryService, Discovery.DiscoveryService>();

        services.AddSingleton<IJobDataService, SqliteJobDataService>();
        services.AddSingleton<FakeJobDataService>(); // for seed-fake command only
        return services;
    }

    private static void ConfigureDapper()
    {
        if (_dapperConfigured) return;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dapperConfigured = true;
    }
}
