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
        services.AddSingleton<ICompanyUrlsRepository, CompanyUrlsRepository>();
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<ILevelRepository, LevelRepository>();
        services.AddSingleton<IAreaRepository, AreaRepository>();
        services.AddSingleton<ISavedFilterRepository, SavedFilterRepository>();
        services.AddSingleton<IDiscoverySeedRepository, DiscoverySeedRepository>();
        services.AddSingleton<ICompanyDiscoveryRepository, CompanyDiscoveryRepository>();
        services.AddSingleton<IDirectoryCrawlRepository, DirectoryCrawlRepository>();
        services.AddSingleton<IAiExtractionCacheRepository, AiExtractionCacheRepository>();
        services.AddSingleton<ITechnologyRepository, TechnologyRepository>();
        services.AddSingleton<Technology.ITechnologyMatcher, Technology.TechnologyMatcher>();

        services.AddSingleton<Classification.HeuristicClassifier>();
        services.AddSingleton<Classification.AiFallbackClassifier>();
        services.AddSingleton<Classification.IJobClassifier, Classification.CompositeClassifier>();
        services.AddSingleton<Classification.IJobReclassifier, Classification.JobReclassifier>();

        services.AddSingleton<ApiUsage.IApiUsageTracker, ApiUsage.ApiUsageTracker>();
        services.AddSingleton<ApiUsage.IApiQuotaController, ApiUsage.ApiQuotaController>();
        services.AddSingleton<Logging.IRunLogger, Logging.RunLogger>();
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
        services.AddHttpClient<Discovery.ICompanyDirectoryHarvester, Discovery.CompanyDirectoryHarvester>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(12);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Jobnet/0.7; +https://github.com/jobnet)");
        });

        // Discovery strategies. Static strategies (web search, AI competitor) are singletons;
        // directory-harvest strategies are built dynamically from the discovery_seeds table.
        services.AddSingleton<Discovery.Strategies.WebSearchStrategy>();
        services.AddSingleton<Discovery.Strategies.AiCompetitorStrategy>();
        services.AddSingleton<Discovery.Strategies.IDiscoveryStrategyProvider, Discovery.Strategies.DiscoveryStrategyProvider>();

        services.AddHttpClient<AtsDetection.IAtsDetector, AtsDetection.AtsDetector>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Jobnet/0.5; +https://github.com/jobnet)");
        });

        services.AddHttpClient<Ai.ClaudeClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.6");
        });
        services.AddHttpClient<Ai.GeminiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.6");
        });
        services.AddHttpClient<Ai.GroqClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.7");
        });
        // In-process local model. Singleton because LLamaWeights is expensive to load and is
        // safe to reuse across calls (each call gets its own StatelessExecutor / context).
        services.AddSingleton<Ai.LLamaClient>();
        services.AddSingleton<Ai.IAiClient, Ai.RoutingAiClient>();

        services.AddSingleton<Playwright.IPlaywrightFetcher, Playwright.PlaywrightFetcher>();

        services.AddHttpClient<Profiling.ICompanyProfiler, Profiling.CompanyProfiler>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Jobnet/0.5; +https://github.com/jobnet)");
        });

        services.AddHttpClient<Summarization.IJobSummarizer, Summarization.JobSummarizer>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Jobnet/0.6; +https://github.com/jobnet)");
        });

        services.AddHttpClient<AtsAdapters.GreenhouseJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.5");
        });
        services.AddHttpClient<AtsAdapters.LeverJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.5");
        });
        services.AddHttpClient<AtsAdapters.AshbyJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.5");
        });
        services.AddSingleton<AtsAdapters.AiExtractedJobSource>();
        services.AddSingleton<AtsAdapters.IAtsJobSource>(sp => sp.GetRequiredService<AtsAdapters.GreenhouseJobSource>());
        services.AddSingleton<AtsAdapters.IAtsJobSource>(sp => sp.GetRequiredService<AtsAdapters.LeverJobSource>());
        services.AddSingleton<AtsAdapters.IAtsJobSource>(sp => sp.GetRequiredService<AtsAdapters.AshbyJobSource>());
        services.AddSingleton<AtsAdapters.IAtsJobSource>(sp => sp.GetRequiredService<AtsAdapters.AiExtractedJobSource>());
        services.AddSingleton<AtsAdapters.IJobRefresher, AtsAdapters.JobRefresher>();
        services.AddHttpClient<AtsAdapters.IJobDetailRefresher, AtsAdapters.JobDetailRefresher>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Jobnet/0.6; +https://github.com/jobnet)");
        });

        services.AddSingleton<Resume.IResumeMatcher, Resume.ResumeMatcher>();
        services.AddSingleton<CoverLetter.ICoverLetterGenerator, CoverLetter.CoverLetterGenerator>();

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
