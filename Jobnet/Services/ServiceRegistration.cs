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

        services.AddHttpClient<JobSources.GreenhouseJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.5");
        });
        services.AddHttpClient<JobSources.LeverJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.5");
        });
        services.AddHttpClient<JobSources.AshbyJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.5");
        });
        services.AddHttpClient<JobSources.BambooHRJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.8");
        });
        services.AddHttpClient<JobSources.WorkdayJobSource>(client =>
        {
            // Workday tenants can be slow on first hit (cold cache) — bump the timeout.
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.8");
        });
        services.AddHttpClient<JobSources.WorkableJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.8");
        });
        services.AddHttpClient<JobSources.SmartRecruitersJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.8");
        });
        services.AddHttpClient<JobSources.PinpointJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/0.9");
        });
        services.AddHttpClient<JobSources.AmazonJobSource>(client =>
        {
            // Amazon's search.json can be slow under load — give it room. Two pages of 100
            // postings is ~200KB total, well within reason but the server response time varies.
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Jobnet/1.0)");
        });
        services.AddHttpClient<JobSources.RisePeopleJobSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/1.0");
        });
        services.AddSingleton<Parsing.SelectorProfileReplayer>();
        services.AddSingleton<Parsing.AiSelectorDeriver>();
        // Hand-written company parsers. Registration order = priority order — more-specific
        // patterns first. The registry is what JobRefresher will probe before falling back to
        // the AI-extract path.
        services.AddSingleton<Parsing.HtmlPatternParsers.IHtmlPatternParser, Parsing.HtmlPatternParsers.LeverShortcodeParser>();
        services.AddSingleton<Parsing.HtmlPatternParsers.IHtmlPatternParser, Parsing.HtmlPatternParsers.GreenhouseLinkParser>();
        services.AddSingleton<Parsing.HtmlPatternParsers.HtmlPatternRegistry>();
        services.AddSingleton<JobSources.AiFallbackJobSource>();
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.GreenhouseJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.LeverJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.AshbyJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.BambooHRJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.WorkdayJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.WorkableJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.SmartRecruitersJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.PinpointJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.AmazonJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.RisePeopleJobSource>());
        services.AddSingleton<JobSources.IJobSource>(sp => sp.GetRequiredService<JobSources.AiFallbackJobSource>());
        services.AddSingleton<JobSources.IJobRefresher, JobSources.JobRefresher>();
        services.AddHttpClient<JobSources.IJobDetailRefresher, JobSources.JobDetailRefresher>(client =>
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
