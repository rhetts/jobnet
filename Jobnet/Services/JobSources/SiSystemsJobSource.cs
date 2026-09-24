using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// S.i. Systems (Canada's largest IT staffing agency, sisystems.com) — a plain, unauthenticated
/// JSON API discovered from a HAR capture of their job-search widget:
/// <code>
/// POST https://www.sisystems.com/api/search/SearchJobs
/// Body: {"keywords":"","locationId":"{slug}","expertiseId":"","opportunityTypeID":"",
///        "orderBy":"date","sorting":"desc","language":"en","isUsa":false,
///        "pageSize":50,"offset":0}
/// Response: { "items": [ { "jobId", "jobTitle", "companyName", "expertise", "jobType",
///     "location", "jobCreatedDate", "payRate", "encryptedJobId", ... } ], "totalItems": N }
/// </code>
/// <c>locationId</c> is the site's own location-filter code — "3" is Vancouver, confirmed by a
/// live capture returning 233 postings all tagged <c>"location":"Vancouver"</c>. Using it means
/// we only ever request the relevant slice of S.i. Systems' 1000+ pan-Canada postings, rather
/// than pulling everything and relying on LocationMatcher to discard most of it afterward.
///
/// Shells out to curl.exe rather than using HttpClient. Their WAF (F5 "volt-adc") fingerprints
/// the TLS handshake itself — confirmed via HAR + live testing that .NET's HttpClient AND
/// PowerShell's Invoke-WebRequest (same underlying SChannel stack) both get a flat "Request
/// Rejected" HTML page back with an HTTP 200, for a byte-identical request that curl (OpenSSL-
/// based build) sails through with. Forcing HTTP/1.1 in .NET made no difference, ruling out an
/// HTTP/2-negotiation explanation. There's no supported way to reshape SChannel's ClientHello
/// from managed code, so curl.exe (present by default on Windows 10/11) does the actual request;
/// this class just builds its arguments and parses its stdout as JSON.
///
/// <c>companyName</c> is the agency's *client* — the actual hiring company (e.g. "BC Hydro",
/// "Teck Resources Ltd.") — not S.i. Systems itself. We fold it into the description snippet
/// rather than the title so postings still read naturally in the jobs list.
/// </summary>
public sealed class SiSystemsJobSource : IJobSource
{
    public const string Provider = "ats_sisystems";
    public string AtsType => "sisystems";

    private const string Endpoint = "https://www.sisystems.com/api/search/SearchJobs";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const int PageSize = 50;
    private const int MaxPages = 30; // safety cap; 30*50 = 1,500 postings, well above any real count
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(20);

    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public SiSystemsJobSource(IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    /// <summary><paramref name="slug"/> is the site's locationId filter code (e.g. "3" for Vancouver).</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var all = new List<RawJobPosting>();
        var offset = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            var body = JsonSerializer.Serialize(new SearchRequest
            {
                LocationId = slug,
                PageSize = PageSize,
                Offset = offset,
            });

            var stdout = await RunCurlAsync(body, ct);

            Response? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Response>(stdout);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"S.i. Systems: curl returned non-JSON for locationId '{slug}' (page {page + 1}): {Trunc(stdout, 200)}", ex);
            }

            var batch = ParseBatch(payload);
            all.AddRange(batch);

            var total = payload?.TotalItems ?? 0;
            offset += PageSize;
            if (batch.Count == 0 || offset >= total) break;
        }
        return all;
    }

    /// <summary>Runs curl.exe as a subprocess and returns its stdout. All arguments go through
    /// ArgumentList (no shell involved), so there's no injection risk even though the body is
    /// built from data we control anyway.</summary>
    private static async Task<string> RunCurlAsync(string jsonBody, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "curl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("-X"); psi.ArgumentList.Add("POST");
        psi.ArgumentList.Add(Endpoint);
        psi.ArgumentList.Add("-H"); psi.ArgumentList.Add("Content-Type: application/json");
        psi.ArgumentList.Add("-H"); psi.ArgumentList.Add("Accept: application/json");
        psi.ArgumentList.Add("-H"); psi.ArgumentList.Add("Origin: https://www.sisystems.com");
        psi.ArgumentList.Add("-H"); psi.ArgumentList.Add("Referer: https://www.sisystems.com/");
        psi.ArgumentList.Add("-A"); psi.ArgumentList.Add(UserAgent);
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(jsonBody);
        psi.ArgumentList.Add("--max-time"); psi.ArgumentList.Add(((int)CurlTimeout.TotalSeconds).ToString());

        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CurlTimeout + TimeSpan.FromSeconds(5)); // belt-and-braces if --max-time doesn't fire

        if (!process.Start())
            throw new InvalidOperationException("S.i. Systems: failed to start curl process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"S.i. Systems: curl exited {process.ExitCode}: {Trunc(stderr, 300)}");

        return stdout;
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>Pulled out for unit testing — convert a deserialised page into RawJobPostings.</summary>
    public static IReadOnlyList<RawJobPosting> ParseBatch(Response? payload)
    {
        var items = payload?.Items ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrWhiteSpace(j.JobId) || string.IsNullOrWhiteSpace(j.JobTitle)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = j.JobId!,
                Title = j.JobTitle!,
                Url = BuildJobUrl(j),
                Location = j.Location,
                RemoteType = null, // not exposed by this endpoint
                EmploymentType = j.JobType,
                Department = j.Expertise,
                DescriptionSnippet = string.IsNullOrWhiteSpace(j.CompanyName) ? null : $"Client: {j.CompanyName}",
                SalaryRange = string.IsNullOrWhiteSpace(j.PayRate) ? null : j.PayRate!.Trim(),
            });
        }
        return results;
    }

    /// <summary>Mirrors the site's own JS: jobDetailsPageUrl + "/" + slug(jobTitle) + "/" + encryptedJobId + "/",
    /// lowercased. Falls back to a facet-free "/jobs/{jobId}/" if encryptedJobId is missing.</summary>
    private static string BuildJobUrl(Posting j)
    {
        if (string.IsNullOrWhiteSpace(j.EncryptedJobId))
            return $"https://www.sisystems.com/jobs/{j.JobId}/";

        var slug = System.Text.RegularExpressions.Regex.Replace(j.JobTitle ?? "", "[^a-zA-Z0-9 ]", " ").Trim();
        slug = System.Text.RegularExpressions.Regex.Replace(slug, " +", "-");
        return $"https://www.sisystems.com/jobs/{slug}/{j.EncryptedJobId}/".ToLowerInvariant();
    }

    public sealed class SearchRequest
    {
        [JsonPropertyName("keywords")]          public string Keywords { get; set; } = "";
        [JsonPropertyName("locationId")]        public string LocationId { get; set; } = "";
        [JsonPropertyName("expertiseId")]       public string ExpertiseId { get; set; } = "";
        [JsonPropertyName("opportunityTypeID")] public string OpportunityTypeId { get; set; } = "";
        [JsonPropertyName("orderBy")]           public string OrderBy { get; set; } = "date";
        [JsonPropertyName("sorting")]           public string Sorting { get; set; } = "desc";
        [JsonPropertyName("language")]          public string Language { get; set; } = "en";
        [JsonPropertyName("isUsa")]             public bool IsUsa { get; set; } = false;
        [JsonPropertyName("pageSize")]          public int PageSize { get; set; }
        [JsonPropertyName("offset")]            public int Offset { get; set; }
    }

    public sealed class Response
    {
        [JsonPropertyName("items")]      public List<Posting>? Items { get; set; }
        [JsonPropertyName("totalItems")] public int? TotalItems { get; set; }
    }

    public sealed class Posting
    {
        [JsonPropertyName("jobId")]          public string? JobId { get; set; }
        [JsonPropertyName("jobTitle")]       public string? JobTitle { get; set; }
        [JsonPropertyName("companyName")]    public string? CompanyName { get; set; }
        [JsonPropertyName("expertise")]      public string? Expertise { get; set; }
        [JsonPropertyName("jobType")]        public string? JobType { get; set; }
        [JsonPropertyName("location")]       public string? Location { get; set; }
        [JsonPropertyName("payRate")]        public string? PayRate { get; set; }
        [JsonPropertyName("encryptedJobId")] public string? EncryptedJobId { get; set; }
    }
}
