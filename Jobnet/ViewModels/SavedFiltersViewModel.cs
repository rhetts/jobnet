using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;

namespace Jobnet.ViewModels;

public partial class SavedFiltersViewModel : ObservableObject
{
    private readonly ISavedFilterRepository _repo;

    public ObservableCollection<SavedFilterItem> Items { get; } = new();

    /// <summary>Set by the caller; invoked when the user clicks Load on a row.</summary>
    public Action<SavedFilter>? OnLoadRequested { get; set; }

    public SavedFiltersViewModel(ISavedFilterRepository repo)
    {
        _repo = repo;
        Reload();
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var f in _repo.GetAll())
            Items.Add(new SavedFilterItem(f, this));
    }

    [RelayCommand]
    private void LoadItem(SavedFilterItem? item)
    {
        if (item is null) return;
        _repo.MarkUsed(item.Source.Id);
        OnLoadRequested?.Invoke(item.Source);
    }

    [RelayCommand]
    private void DeleteItem(SavedFilterItem? item)
    {
        if (item is null) return;
        _repo.Delete(item.Source.Id);
        Items.Remove(item);
    }

    [RelayCommand]
    private void RenameItem(SavedFilterItem? item)
    {
        if (item is null) return;
        var newName = Views.TextPromptWindow.Ask(
            System.Windows.Application.Current.MainWindow,
            title: "Rename filter",
            prompt: "New name:",
            initialValue: item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        try
        {
            _repo.Rename(item.Source.Id, newName);
            Reload();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Rename failed (name might already exist): {ex.Message}",
                "Rename filter", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}

public partial class SavedFilterItem : ObservableObject
{
    public SavedFilter Source { get; }
    public string Name => Source.Name;
    public string CreatedDisplay => Source.DateCreated.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string UsedDisplay => Source.DateUsed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(never)";

    /// <summary>Held so XAML bindings to parent commands work via RelativeSource AncestorType=Window.</summary>
    public SavedFiltersViewModel Parent { get; }

    public SavedFilterItem(SavedFilter source, SavedFiltersViewModel parent)
    {
        Source = source;
        Parent = parent;
    }
}
