using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class CompanyRepository : ICompanyRepository
{
    private const string SelectAll = @"
        SELECT id, name, domain, website_url, careers_url, ats_type, ats_slug,
               ats_department_filter,
               parser_strategy, industry_tags, city, interest_level, notes,
               date_discovered, date_last_scan, is_active, is_agency,
               parser_strategy_disabled, parser_strategy_derived_at,
               parser_strategy_last_result, parser_strategy_last_result_at,
               parser_strategy_last_error
        FROM companies";

    private readonly IDbConnectionFactory _connections;

    public CompanyRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<Company> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<CompanyRow>($"{SelectAll} ORDER BY name")
            .Select(MapToCompany)
            .ToList();
    }

    public Company? GetById(int id)
    {
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<CompanyRow>($"{SelectAll} WHERE id = @id", new { id });
        return row is null ? null : MapToCompany(row);
    }

    public Company? GetByDomain(string domain)
    {
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<CompanyRow>(
            $"{SelectAll} WHERE LOWER(domain) = LOWER(@domain)", new { domain });
        return row is null ? null : MapToCompany(row);
    }

    public int Insert(Company company)
    {
        using var conn = _connections.Open();
        return (int)conn.ExecuteScalar<long>(@"
            INSERT INTO companies (name, domain, website_url, careers_url, ats_type, ats_slug,
                                   parser_strategy, city, interest_level, notes, date_discovered,
                                   date_last_scan, is_active, is_agency)
            VALUES (@Name, @Domain, @WebsiteUrl, @CareersUrl, @AtsType, NULL, NULL, @City,
                    @InterestLevelText, NULL, @DateDiscoveredText, @DateLastScanText, 1, @IsAgencyInt);
            SELECT last_insert_rowid();",
            new
            {
                company.Name,
                company.Domain,
                company.WebsiteUrl,
                company.CareersUrl,
                company.AtsType,
                company.City,
                InterestLevelText = ToDbText(company.InterestLevel),
                DateDiscoveredText = company.DateDiscovered.ToUniversalTime().ToString("o"),
                DateLastScanText = company.DateLastScan?.ToUniversalTime().ToString("o"),
                IsAgencyInt = company.IsAgency ? 1 : 0,
            });
    }

    public void Update(Company company)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE companies SET
                name = @Name,
                website_url = @WebsiteUrl,
                careers_url = @CareersUrl,
                ats_type = @AtsType,
                city = @City,
                interest_level = @InterestLevelText
            WHERE id = @Id",
            new
            {
                company.Id,
                company.Name,
                company.WebsiteUrl,
                company.CareersUrl,
                company.AtsType,
                company.City,
                InterestLevelText = ToDbText(company.InterestLevel)
            });
    }

    public void SetInterestLevel(int id, InterestLevel level)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE companies SET interest_level = @lvl WHERE id = @id",
            new { id, lvl = ToDbText(level) });
    }

    public void SetLastScan(int id, DateTime when)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE companies SET date_last_scan = @when WHERE id = @id",
            new { id, when = when.ToUniversalTime().ToString("o") });
    }

    public void SetAtsInfo(int id, string? atsType, string? atsSlug, string? careersUrl)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE companies SET ats_type = @atsType, ats_slug = @atsSlug,
                                 careers_url = COALESCE(@careersUrl, careers_url)
            WHERE id = @id",
            new { id, atsType, atsSlug, careersUrl });
    }

    public Models.CompanyProfile? GetProfile(int companyId)
    {
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<(string? Summary, string? Products, string? Industries,
            string? Signals, string? Hq, string? Size, string? GeneratedAt, string? Model)>(@"
            SELECT profile_summary AS Summary, profile_products AS Products,
                   profile_industries AS Industries, profile_tech_signals AS Signals,
                   profile_hq_hint AS Hq, profile_size_hint AS Size,
                   profile_generated_at AS GeneratedAt, profile_model AS Model
            FROM companies WHERE id = @id", new { id = companyId });

        if (row.Summary is null && row.Products is null && row.Industries is null) return null;

        return new Models.CompanyProfile
        {
            Summary = row.Summary,
            Products = ParseArr(row.Products),
            Industries = ParseArr(row.Industries),
            TechSignals = ParseArr(row.Signals),
            HeadquartersHint = row.Hq,
            SizeHint = row.Size,
            GeneratedAt = string.IsNullOrEmpty(row.GeneratedAt) ? null : DateTime.Parse(row.GeneratedAt).ToUniversalTime(),
            Model = row.Model,
        };
    }

    private static IReadOnlyList<string> ParseArr(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return Array.Empty<string>(); }
    }

    private static Company MapToCompany(CompanyRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Domain = r.Domain,
        WebsiteUrl = r.WebsiteUrl,
        CareersUrl = r.CareersUrl,
        AtsType = r.AtsType,
        AtsSlug = r.AtsSlug,
        AtsDepartmentFilter = r.AtsDepartmentFilter,
        City = r.City,
        InterestLevel = ParseInterest(r.InterestLevel),
        DateDiscovered = DateTime.Parse(r.DateDiscovered).ToUniversalTime(),
        DateLastScan = string.IsNullOrEmpty(r.DateLastScan) ? null : DateTime.Parse(r.DateLastScan).ToUniversalTime(),
        IsAgency = r.IsAgency != 0,
        ParserStrategy = r.ParserStrategy,
        ParserStrategyDisabled = r.ParserStrategyDisabled != 0,
        ParserStrategyDerivedAt = ParseUtc(r.ParserStrategyDerivedAt),
        ParserStrategyLastResult = r.ParserStrategyLastResult,
        ParserStrategyLastResultAt = ParseUtc(r.ParserStrategyLastResultAt),
        ParserStrategyLastError = r.ParserStrategyLastError,
    };

    private static DateTime? ParseUtc(string? value)
        => string.IsNullOrEmpty(value) ? null : DateTime.Parse(value).ToUniversalTime();

    public void SetParserStrategy(int id, string profileJson, DateTime derivedAt)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE companies SET
                parser_strategy = @profileJson,
                parser_strategy_derived_at = @when,
                parser_strategy_last_result = NULL,
                parser_strategy_last_result_at = NULL,
                parser_strategy_last_error = NULL
            WHERE id = @id",
            new { id, profileJson, when = derivedAt.ToUniversalTime().ToString("o") });
    }

    public void SetParserStrategyResult(int id, string result, DateTime when, string? errorMessage)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE companies SET
                parser_strategy_last_result = @result,
                parser_strategy_last_result_at = @when,
                parser_strategy_last_error = @errorMessage
            WHERE id = @id",
            new { id, result, when = when.ToUniversalTime().ToString("o"), errorMessage });
    }

    public void ClearParserStrategy(int id)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE companies SET
                parser_strategy = NULL,
                parser_strategy_derived_at = NULL,
                parser_strategy_last_result = NULL,
                parser_strategy_last_result_at = NULL,
                parser_strategy_last_error = NULL
            WHERE id = @id", new { id });
    }

    public void SetParserStrategyDisabled(int id, bool disabled)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE companies SET parser_strategy_disabled = @flag WHERE id = @id",
            new { id, flag = disabled ? 1 : 0 });
    }

    // Same caveat as JobRepository.ToDbText — DB CHECK forces 'interesting' on disk for the
    // Approved enum value. Parse accepts both spellings.
    private static string? ToDbText(InterestLevel level) => level switch
    {
        InterestLevel.Approved       => "interesting",
        InterestLevel.NotInteresting => "not_interesting",
        _                            => null
    };

    private static InterestLevel ParseInterest(string? value) => value switch
    {
        "approved" or "interesting" => InterestLevel.Approved,
        "not_interesting"           => InterestLevel.NotInteresting,
        _                           => InterestLevel.Neutral
    };

    private sealed class CompanyRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string? WebsiteUrl { get; set; }
        public string? CareersUrl { get; set; }
        public string? AtsType { get; set; }
        public string? AtsSlug { get; set; }
        public string? AtsDepartmentFilter { get; set; }
        public string? ParserStrategy { get; set; }
        public string? IndustryTags { get; set; }
        public string? City { get; set; }
        public string? InterestLevel { get; set; }
        public string? Notes { get; set; }
        public string DateDiscovered { get; set; } = string.Empty;
        public string? DateLastScan { get; set; }
        public int IsActive { get; set; }
        public int IsAgency { get; set; }
        public int ParserStrategyDisabled { get; set; }
        public string? ParserStrategyDerivedAt { get; set; }
        public string? ParserStrategyLastResult { get; set; }
        public string? ParserStrategyLastResultAt { get; set; }
        public string? ParserStrategyLastError { get; set; }
    }
}
