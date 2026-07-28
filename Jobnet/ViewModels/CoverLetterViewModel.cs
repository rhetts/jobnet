using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.CoverLetter;

namespace Jobnet.ViewModels;

public partial class CoverLetterViewModel : ObservableObject
{
    private readonly ICoverLetterGenerator _generator;
    private readonly IConfigRepository _config;
    private readonly ICoverLetterRepository _letters;
    private readonly ICoverLetterPdfWriter _pdf;
    private Job? _job;
    private string _companyName = "";
    private string? _lastModel;
    private bool _suppressTextSave;
    private DispatcherTimer? _debounceTimer;

    /// <summary>URL passed to Load via <see cref="LoadFromUrl"/>. When set, Generate uses the
    /// URL-based code path and we skip DB persistence (no job_id to key on). The title and
    /// company are inferred from the AI response after the first generation.</summary>
    private string? _pastedUrl;

    /// <summary>Role title extracted by the AI from the URL page. Null until generation succeeds
    /// OR if the AI didn't return a TITLE marker — SaveToFile falls back to a URL-derived name
    /// in that case so the user always gets a sensible filename.</summary>
    private string? _detectedTitle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveToFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _headerLine = "Cover letter";

    [ObservableProperty]
    private string _letterText = "";

    [ObservableProperty]
    private string _statusLine = "Click Generate to create a cover letter for this job.";

    [ObservableProperty]
    private string _instructionsText = "";

    [ObservableProperty]
    private string _saveDirectory = "";

    public CoverLetterViewModel(ICoverLetterGenerator generator, IConfigRepository config,
                                ICoverLetterRepository letters, ICoverLetterPdfWriter pdf)
    {
        _generator = generator;
        _config = config;
        _letters = letters;
        _pdf = pdf;
        _instructionsText = config.GetOrDefault("cover_letter_instructions", "");
        _saveDirectory = config.GetOrDefault("cover_letter_save_directory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Jobnet-CoverLetters"));
    }

    public void Load(Job job, string companyName)
    {
        _job = job;
        _companyName = companyName;
        _pastedUrl = null;
        HeaderLine = $"{job.Title} — {companyName}";

        var existing = _letters.Get(job.Id);
        _suppressTextSave = true;
        try
        {
            if (existing is null)
            {
                LetterText = "";
                _lastModel = null;
                StatusLine = "Click Generate to create a cover letter for this job.";
            }
            else
            {
                LetterText = existing.Text;
                _lastModel = existing.Model;
                var when = existing.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var modelBit = string.IsNullOrEmpty(existing.Model) ? "" : $" · {existing.Model}";
                StatusLine = $"Loaded saved letter (updated {when}{modelBit}).";
            }
        }
        finally { _suppressTextSave = false; }
    }

    /// <summary>URL-based entry point. No Job exists yet; the AI is asked to identify the role
    /// and company from the rendered page and write the letter in one call. DB persistence is
    /// skipped (no job_id), but Copy and Save-to-PDF still work using whatever title/company
    /// the AI returns. Generation kicks off immediately so the user doesn't need a second click.</summary>
    public void LoadFromUrl(string url)
    {
        _job = null;
        _companyName = "";
        _pastedUrl = url;
        _detectedTitle = null;     // populated by the AI; SaveToFile has a URL-derived fallback
        HeaderLine = $"From URL: {url}";
        _suppressTextSave = true;
        try
        {
            LetterText = "";
            _lastModel = null;
            StatusLine = "Fetching page and generating cover letter...";
        }
        finally { _suppressTextSave = false; }

        // Fire-and-forget — the command handler is async so we don't block Load(). The window
        // is already shown by the caller before this runs.
        _ = GenerateFromUrlAsync();
    }

    private bool CanGenerate() => !IsBusy && (_job is not null || _pastedUrl is not null);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        // Generate-button entrypoint routes to whichever flow was loaded. URL mode is the
        // ad-hoc "paste a URL" path; Job mode is the regular sidebar-driven path.
        if (_pastedUrl is not null) { await GenerateFromUrlAsync(); return; }
        if (_job is null) return;

        _config.Set("cover_letter_instructions", InstructionsText ?? "");
        _config.Set("cover_letter_save_directory", SaveDirectory ?? "");

        IsBusy = true;
        StatusLine = "Generating cover letter...";
        try
        {
            var r = await Task.Run(() => _generator.GenerateAsync(_job, _companyName)).ConfigureAwait(true);
            if (r.Success)
            {
                _suppressTextSave = true;
                try { LetterText = r.Text ?? ""; }
                finally { _suppressTextSave = false; }

                _lastModel = r.Model;
                _letters.SaveGenerated(_job.Id, LetterText, r.Model, DateTime.UtcNow);
                StatusLine = $"Generated · saved to DB · {r.InputTokens} in / {r.OutputTokens} out tokens · model {r.Model}";
            }
            else
            {
                StatusLine = $"Generation failed: {r.Error}";
            }
        }
        catch (Exception ex) { StatusLine = $"Failed: {ex.GetType().Name}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task GenerateFromUrlAsync()
    {
        if (_pastedUrl is null) return;
        _config.Set("cover_letter_instructions", InstructionsText ?? "");
        _config.Set("cover_letter_save_directory", SaveDirectory ?? "");

        IsBusy = true;
        StatusLine = "Fetching page and generating cover letter...";
        try
        {
            var url = _pastedUrl;
            var r = await Task.Run(() => _generator.GenerateFromUrlAsync(url)).ConfigureAwait(true);
            if (r.Success)
            {
                _suppressTextSave = true;
                try { LetterText = r.Text ?? ""; }
                finally { _suppressTextSave = false; }

                _lastModel = r.Model;
                if (!string.IsNullOrWhiteSpace(r.DetectedCompany)) _companyName    = r.DetectedCompany!.Trim();
                if (!string.IsNullOrWhiteSpace(r.DetectedTitle))   _detectedTitle  = r.DetectedTitle!.Trim();

                // Update the header to read like the imported-job flow ("Title — Company") so the
                // user sees what the AI identified.
                var headerParts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(_detectedTitle)) headerParts.Add(_detectedTitle!);
                if (!string.IsNullOrWhiteSpace(_companyName))   headerParts.Add(_companyName);
                if (headerParts.Count > 0) HeaderLine = string.Join(" — ", headerParts);

                StatusLine = $"Generated · {r.InputTokens} in / {r.OutputTokens} out tokens · model {r.Model} · (URL mode, not saved to DB)";
            }
            else
            {
                StatusLine = $"Generation failed: {r.Error}";
            }
        }
        catch (Exception ex) { StatusLine = $"Failed: {ex.GetType().Name}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool CanSaveOrCopy() => !IsBusy && !string.IsNullOrWhiteSpace(LetterText);

    [RelayCommand(CanExecute = nameof(CanSaveOrCopy))]
    private void Copy()
    {
        try { Clipboard.SetText(LetterText); StatusLine = "Copied to clipboard."; }
        catch (Exception ex) { StatusLine = $"Copy failed: {ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanSaveOrCopy))]
    private void SaveToFile()
    {
        // Imported job: title/company come from the Job row. URL mode: from the AI's response,
        // with URL-derived fallbacks if the AI didn't identify them. Either way the filename
        // ends up as "{Company} {Title}.pdf", matching the imported-job flow.
        var (title, company) = ResolveTitleAndCompanyForSave();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(company))
        {
            StatusLine = "Couldn't infer a filename — generate first, then try again.";
            return;
        }
        try
        {
            // Flush any pending debounced edit before exporting — guarantees the PDF reflects
            // exactly what's in the textbox.
            FlushPendingTextSave();

            var dir = SaveDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(dir)) { StatusLine = "Save folder is empty (set it on the Instructions tab)."; return; }
            Directory.CreateDirectory(dir);
            var fullPath = BuildUniquePath(dir, company, title);
            _pdf.Write(fullPath, company, title, LetterText);
            _config.Set("cover_letter_save_directory", dir);
            StatusLine = $"Saved PDF → {fullPath}";
        }
        catch (Exception ex) { StatusLine = $"PDF save failed: {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>Resolve (title, company) for the PDF filename. Imported-job mode uses the Job
    /// fields directly. URL mode prefers the AI-detected fields and falls back to a URL-host
    /// guess so the file always lands with a recognisable name.</summary>
    private (string Title, string Company) ResolveTitleAndCompanyForSave()
    {
        if (_job is not null)
            return (_job.Title ?? "", _companyName ?? "");

        // URL mode.
        var title   = _detectedTitle ?? "";
        var company = _companyName ?? "";

        if (string.IsNullOrWhiteSpace(company) && _pastedUrl is not null)
        {
            // Pull the registrable host fragment as a company-name guess. "jobs.shakepay.com" → "shakepay".
            try
            {
                var host = new Uri(_pastedUrl).Host;
                var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) company = parts[^2];  // skip TLD
                else if (parts.Length == 1) company = parts[0];
                // Title-case the fragment so "shakepay" becomes "Shakepay" in the filename.
                if (!string.IsNullOrEmpty(company))
                    company = char.ToUpperInvariant(company[0]) + company[1..];
            }
            catch { /* malformed URI is fine — title-only filename below */ }
        }

        if (string.IsNullOrWhiteSpace(title))
            title = "Cover letter";

        return (title, company);
    }

    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select cover letter save folder",
            InitialDirectory = string.IsNullOrWhiteSpace(SaveDirectory) ? null : SaveDirectory,
        };
        if (dlg.ShowDialog() == true)
        {
            SaveDirectory = dlg.FolderName;
            _config.Set("cover_letter_save_directory", SaveDirectory);
        }
    }

    partial void OnInstructionsTextChanged(string value) => _config.Set("cover_letter_instructions", value ?? "");
    partial void OnSaveDirectoryChanged(string value)    => _config.Set("cover_letter_save_directory", value ?? "");

    partial void OnLetterTextChanged(string value)
    {
        // Debounced DB write of manual edits. Suppressed during Load() and during Generate's
        // own assignment — both of those persist explicitly via the repo.
        if (_suppressTextSave || _job is null) return;
        if (_debounceTimer is null)
        {
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _debounceTimer.Tick += (_, _) => FlushPendingTextSave();
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void FlushPendingTextSave()
    {
        if (_debounceTimer is null || !_debounceTimer.IsEnabled) return;
        _debounceTimer.Stop();
        if (_job is null) return;
        try
        {
            // UpdateText is a no-op if no row exists yet — a user typing into the box before
            // Generate has ever run won't materialise a half-baked row.
            _letters.UpdateText(_job.Id, LetterText ?? "");
        }
        catch
        {
            // Persistence failures shouldn't crash the UI; the next debounce tick or explicit
            // save will retry.
        }
    }

    /// <summary>Called by the view on close to make sure any pending edit reaches the DB.</summary>
    public void Flush() => FlushPendingTextSave();

    private static string BuildUniquePath(string dir, string company, string title)
    {
        // Short, human filename: "Amazon Senior Software Engineer.pdf". No timestamp — collisions
        // would be a same-day re-save which we disambiguate with " (2)", " (3)", ... suffixes.
        var baseName = $"{SafeFileSegment(company)} {SafeFileSegment(title)}".Trim();
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "cover-letter";

        var path = Path.Combine(dir, baseName + ".pdf");
        var n = 2;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{baseName} ({n++}).pdf");
        return path;
    }

    private static string SafeFileSegment(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        // Collapse runs of whitespace so "Foo   Bar" doesn't leak through.
        var clean = System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
        return clean;
    }
}
