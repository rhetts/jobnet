using System;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using Jobnet.Services.Resume;

namespace Jobnet.Services.CoverLetter;

public sealed class CoverLetterGenerator : ICoverLetterGenerator
{
    private readonly IAiClient _ai;
    private readonly IConfigRepository _config;
    private readonly IResumeMatcher _resume;
    private readonly ICompanyRepository _companies;

    public CoverLetterGenerator(IAiClient ai, IConfigRepository config,
                                  IResumeMatcher resume, ICompanyRepository companies)
    {
        _ai = ai;
        _config = config;
        _resume = resume;
        _companies = companies;
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
}
