using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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
    private Job? _job;
    private string _companyName = "";

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

    public CoverLetterViewModel(ICoverLetterGenerator generator, IConfigRepository config)
    {
        _generator = generator;
        _config = config;
        _instructionsText = config.GetOrDefault("cover_letter_instructions", "");
        _saveDirectory = config.GetOrDefault("cover_letter_save_directory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Jobnet-CoverLetters"));
    }

    public void Load(Job job, string companyName)
    {
        _job = job;
        _companyName = companyName;
        HeaderLine = $"{job.Title} — {companyName}";
        LetterText = "";
        StatusLine = "Click Generate to create a cover letter for this job.";
    }

    private bool CanGenerate() => !IsBusy && _job is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (_job is null) return;
        // Save instructions whenever we generate so they're always persisted.
        _config.Set("cover_letter_instructions", InstructionsText ?? "");
        _config.Set("cover_letter_save_directory", SaveDirectory ?? "");

        IsBusy = true;
        StatusLine = "Generating cover letter (Gemini)...";
        try
        {
            var r = await Task.Run(() => _generator.GenerateAsync(_job, _companyName)).ConfigureAwait(true);
            if (r.Success)
            {
                LetterText = r.Text ?? "";
                StatusLine = $"Generated · {r.InputTokens} in / {r.OutputTokens} out tokens · model {r.Model}";
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
        if (_job is null) return;
        try
        {
            var dir = SaveDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(dir)) { StatusLine = "Save directory is empty (set it on the Instructions tab)."; return; }
            Directory.CreateDirectory(dir);
            var safeCompany = SafeFileName(_companyName);
            var safeTitle   = SafeFileName(_job.Title);
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var filename = $"{safeCompany}_{safeTitle}_{ts}.txt";
            var fullPath = Path.Combine(dir, filename);
            File.WriteAllText(fullPath, LetterText);
            _config.Set("cover_letter_save_directory", dir);
            StatusLine = $"Saved → {fullPath}";
        }
        catch (Exception ex) { StatusLine = $"Save failed: {ex.GetType().Name}: {ex.Message}"; }
    }

    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        // Use Win32 folder picker (introduced in .NET 8 WPF). Fallback gracefully if user cancels.
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

    private static string SafeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "untitled";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        var clean = sb.ToString().Trim().Replace(' ', '-');
        return clean.Length > 60 ? clean.Substring(0, 60) : clean;
    }
}
