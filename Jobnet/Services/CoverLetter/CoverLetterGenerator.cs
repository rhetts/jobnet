using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using Jobnet.Services.Playwright;
using Jobnet.Services.Resume;

namespace Jobnet.Services.CoverLetter;

public sealed class CoverLetterGenerator : ICoverLetterGenerator
{
    private readonly IAiClient _ai;
    private readonly IConfigRepository _config;
    private readonly IResumeMatcher _resume;
    private readonly ICompanyRepository _companies;
    private readonly IPlaywrightFetcher? _fetcher;

    public CoverLetterGenerator(IAiClient ai, IConfigRepository config,
                                  IResumeMatcher resume, ICompanyRepository companies,
                                  IPlaywrightFetcher? fetcher = null)
    {
        _ai = ai;
        _config = config;
        _resume = resume;
        _companies = companies;
        // Fetcher is optional in tests; production DI supplies it. URL-mode generation requires it.
        _fetcher = fetcher;
    }

    public async Task<CoverLetterResult> GenerateAsync(Job job, string companyName, CancellationToken ct = default)
    {
        if (!_ai.IsConfigured)
            return new CoverLetterResult { Success = false, Error = "AI provider not configured (set Gemini key in Settings)." };

        var resume = _resume.GetStoredResume();
        if (string.IsNullOrWhiteSpace(resume))
            return new CoverLetterResult { Success = false, Error = "No resume loaded. Upload one via the Resume button." };

        // Cap resume length to keep the prompt within token budgets.
        if (resume!.Length > 6000) resume = resume.Substring(0, 6000);

        var userInstructions = _config.GetOrDefault("cover_letter_instructions", "").Trim();

        // Pull the company profile if we have one — it gives the AI context on what the company
        // actually does, which makes the letter sound less generic.
        var profile = _companies.GetProfile(job.CompanyId);
        var companyBlock = "";
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.Summary))
        {
            companyBlock = $"\nABOUT THE COMPANY:\n{profile.Summary}";
            if (profile.Products is { Count: > 0 })
                companyBlock += $"\nProducts: {string.Join(", ", profile.Products.Take(5))}";
            if (profile.Industries is { Count: > 0 })
                companyBlock += $"\nIndustries: {string.Join(", ", profile.Industries.Take(5))}";
            if (profile.TechSignals is { Count: > 0 })
                companyBlock += $"\nTech: {string.Join(", ", profile.TechSignals.Take(5))}";
            companyBlock += "\n";
        }

        var jobBlock =
            $"Job title: {job.Title}\n" +
            $"Company:   {companyName}\n" +
            (string.IsNullOrWhiteSpace(job.Location) ? "" : $"Location:  {job.Location}\n") +
            (string.IsNullOrWhiteSpace(job.Summary) ? "" : $"Summary:   {job.Summary}\n") +
            (string.IsNullOrWhiteSpace(job.DescriptionSnippet) ? "" : $"Description: {Trunc(job.DescriptionSnippet!, 2500)}\n");

        var system =
            "You write professional cover letters. Output PLAIN PROSE only — no markdown headers, no bullets, no quote blocks, no 'Dear Hiring Manager' if the user's instructions say otherwise. " +
            "Structure: 3 short paragraphs. " +
            "Paragraph 1: open with the role + a one-line hook tying the candidate's experience to it. " +
            "Paragraph 2: 2-4 sentences matching specific items from the candidate's resume to the job's listed needs. Be concrete — name technologies, scale of teams, outcomes. " +
            "Paragraph 3: close with availability + a sentence inviting next steps. " +
            "Total length: ~250-350 words. Honest, not florid. Never invent experience not in the resume. " +
            "Always honor the user's durable instructions when they conflict with these defaults.";

        var user =
            "RESUME:\n" + resume + "\n\n" +
            "JOB:\n" + jobBlock + companyBlock + "\n" +
            (string.IsNullOrWhiteSpace(userInstructions)
                ? ""
                : $"USER INSTRUCTIONS (overrides above defaults if in conflict):\n{userInstructions}\n\n") +
            "Write the cover letter now.";

        AiResponse response;
        try
        {
            response = await _ai.CompleteAsync(user, system, maxTokens: 2048, ct, task: "cover_letter");
        }
        catch (Exception ex)
        {
            return new CoverLetterResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }

        var text = (response.Text ?? "").Trim();
        if (text.Length < 50)
            return new CoverLetterResult { Success = false, Error = $"AI returned an unusably short response ({text.Length} chars)." };

        return new CoverLetterResult
        {
            Success = true,
            Text = text,
            Model = response.Model,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
        };
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    public async Task<CoverLetterResult> GenerateFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (!_ai.IsConfigured)
            return new CoverLetterResult { Success = false, Error = "AI provider not configured (set Gemini key in Settings)." };

        if (_fetcher is null)
            return new CoverLetterResult { Success = false, Error = "Page fetcher not available — URL-mode requires the Playwright service." };

        var resume = _resume.GetStoredResume();
        if (string.IsNullOrWhiteSpace(resume))
            return new CoverLetterResult { Success = false, Error = "No resume loaded. Upload one via the Resume button." };
        if (resume!.Length > 6000) resume = resume.Substring(0, 6000);

        // 1) Render the page. Playwright handles JS-rendered ATS pages where a raw HttpClient
        //    fetch would return an empty shell.
        string pageText;
        try
        {
            var fetched = await _fetcher.FetchAsync(url, ct);
            if (!fetched.Success || string.IsNullOrWhiteSpace(fetched.Html))
                return new CoverLetterResult { Success = false, Error = $"Fetched 0 bytes from {url} (HTTP {fetched.HttpStatus}). {fetched.Error}".Trim() };
            pageText = ExtractVisibleText(fetched.Html);
            if (pageText.Length < 200)
                return new CoverLetterResult { Success = false, Error = $"Page text too short ({pageText.Length} chars) — likely a login wall or JS-only page." };
        }
        catch (Exception ex)
        {
            return new CoverLetterResult { Success = false, Error = $"Fetch failed: {ex.GetType().Name}: {ex.Message}" };
        }

        // Cap so we don't blow the input token budget on a sprawling careers page.
        if (pageText.Length > 8000) pageText = pageText.Substring(0, 8000);

        var userInstructions = _config.GetOrDefault("cover_letter_instructions", "").Trim();

        // 2) One combined prompt that does both extraction AND writing. Delimiter format
        //    instead of JSON because models routinely emit raw newlines inside the JSON
        //    string for multi-line text, which breaks any strict JSON parser. With this
        //    layout the letter body is just everything after the LETTER marker — newlines
        //    are part of the data, not a syntax error.
        var system =
            "You are reading a job posting and writing a cover letter for the candidate whose resume follows. " +
            "Output EXACTLY three sections, in this order, with the markers verbatim:\n" +
            "TITLE: <role title on one line>\n" +
            "COMPANY: <company name on one line>\n" +
            "LETTER:\n" +
            "<cover letter prose, may span multiple paragraphs>\n\n" +
            "No JSON, no markdown headers, no preamble before TITLE:. " +
            "Letter rules: PLAIN PROSE only — no markdown, no bullets. " +
            "Structure: 3 short paragraphs. " +
            "Paragraph 1: open with the role + a one-line hook tying the candidate's experience to it. " +
            "Paragraph 2: 2-4 sentences matching specific items from the candidate's resume to the job's listed needs. Be concrete. " +
            "Paragraph 3: close with availability + a sentence inviting next steps. " +
            "Total length: ~250-350 words. Honest, not florid. Never invent experience not in the resume. " +
            "Always honor the user's durable instructions when they conflict with these defaults.";

        var user =
            "RESUME:\n" + resume + "\n\n" +
            "JOB POSTING (extracted from " + url + "):\n" + pageText + "\n\n" +
            (string.IsNullOrWhiteSpace(userInstructions)
                ? ""
                : $"USER INSTRUCTIONS (overrides above defaults if in conflict):\n{userInstructions}\n\n") +
            "Output the three sections now, starting with TITLE: on the first line.";

        AiResponse response;
        try
        {
            response = await _ai.CompleteAsync(user, system, maxTokens: 2048, ct, task: "cover_letter");
        }
        catch (Exception ex)
        {
            return new CoverLetterResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }

        var raw = (response.Text ?? "").Trim();
        if (raw.StartsWith("```")) raw = StripCodeFence(raw);

        var (title, company, letter) = ParseTitledLetter(raw);
        if (string.IsNullOrWhiteSpace(letter) || letter!.Length < 50)
            return new CoverLetterResult { Success = false, Error = $"AI returned an unusably short letter ({letter?.Length ?? 0} chars). Raw response: {Trunc(raw, 400)}" };

        return new CoverLetterResult
        {
            Success = true,
            Text = letter.Trim(),
            DetectedTitle = string.IsNullOrWhiteSpace(title) ? null : title!.Trim(),
            DetectedCompany = string.IsNullOrWhiteSpace(company) ? null : company!.Trim(),
            Model = response.Model,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
        };
    }

    /// <summary>Pull just the visible text from an HTML page. AngleSharp's text-of-the-document
    /// gives us a reasonable approximation without the script/style noise. Falls back to the
    /// raw HTML on parse failure.</summary>
    private static string ExtractVisibleText(string html)
    {
        try
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument(html);
            foreach (var n in doc.QuerySelectorAll("script, style, noscript, nav, header, footer"))
                n.Remove();
            var body = doc.Body;
            var text = body?.TextContent ?? doc.DocumentElement.TextContent;
            // Collapse runs of whitespace so the AI doesn't waste tokens on blank lines.
            return System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
        }
        catch { return html; }
    }

    private static string StripCodeFence(string s)
    {
        // Some models still wrap JSON in ```json ... ``` despite "no markdown" — strip it.
        var lines = s.Split('\n');
        var first = 0;
        var last = lines.Length;
        if (lines.Length > 0 && lines[0].StartsWith("```")) first = 1;
        if (lines.Length > 1 && lines[^1].StartsWith("```")) last = lines.Length - 1;
        return string.Join('\n', lines[first..last]).Trim();
    }

    /// <summary>Parse the delimiter-formatted response from <c>GenerateFromUrlAsync</c>.
    /// Expected layout:
    ///   <code>TITLE: ...
    ///   COMPANY: ...
    ///   LETTER:
    ///   ...body...</code>
    /// Tolerates variants: case-insensitive markers, missing TITLE/COMPANY (returns nulls so
    /// the UI can still render the letter), or no LETTER marker (falls back to "everything
    /// after the COMPANY line"). Also handles the legacy JSON-style response defensively —
    /// if the AI ignored instructions and emitted JSON, we still extract the fields when we
    /// can rather than failing the call.</summary>
    private static (string? Title, string? Company, string? Letter) ParseTitledLetter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null, null);

        // Quick JSON-shape sniff. If the AI emitted JSON and it happens to be valid, use it.
        // If it's invalid JSON (the original bug), we fall through to the line parser which
        // handles the human-readable form.
        if (raw.TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                string? t = null, c = null, l = null;
                if (doc.RootElement.TryGetProperty("title", out var tv))   t = tv.GetString();
                if (doc.RootElement.TryGetProperty("company", out var cv)) c = cv.GetString();
                if (doc.RootElement.TryGetProperty("letter", out var lv))  l = lv.GetString();
                if (!string.IsNullOrWhiteSpace(l)) return (t, c, l);
            }
            catch (JsonException) { /* fall through */ }
        }

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        string? title = null, company = null;
        var letterStart = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (title is null   && trimmed.StartsWith("TITLE:",   StringComparison.OrdinalIgnoreCase))
                title   = trimmed.Substring(6).Trim().Trim('"');
            else if (company is null && trimmed.StartsWith("COMPANY:", StringComparison.OrdinalIgnoreCase))
                company = trimmed.Substring(8).Trim().Trim('"');
            else if (trimmed.StartsWith("LETTER:", StringComparison.OrdinalIgnoreCase))
            {
                // LETTER: can either be a marker on its own line OR have text on the same line.
                // Capture both cases.
                var rest = trimmed.Substring(7).Trim();
                letterStart = i + 1;
                if (!string.IsNullOrEmpty(rest))
                {
                    // letter body starts inline — collect that segment + everything after
                    var body = new System.Text.StringBuilder();
                    body.AppendLine(rest);
                    for (var j = i + 1; j < lines.Length; j++) body.AppendLine(lines[j]);
                    return (title, company, body.ToString().Trim());
                }
                break;
            }
        }

        if (letterStart >= 0)
        {
            var body = string.Join('\n', lines, letterStart, lines.Length - letterStart).Trim();
            // Strip any trailing markdown fences the model occasionally adds.
            if (body.EndsWith("```")) body = body[..^3].TrimEnd();
            return (title, company, body);
        }

        // No LETTER: marker found. If we have a TITLE and/or COMPANY, return the remainder.
        // Otherwise, treat the entire response as the letter — better to show something than fail.
        if (title is null && company is null) return (null, null, raw.Trim());
        var afterMeta = string.Join('\n', lines.SkipWhile(l =>
            l.TrimStart().StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)
            || l.TrimStart().StartsWith("COMPANY:", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(l))).Trim();
        return (title, company, afterMeta);
    }
}
