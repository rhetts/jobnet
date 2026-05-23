using System.Windows;
using System.Windows.Input;

namespace Jobnet.Views;

public partial class TextPromptWindow : Window
{
    public string Result { get; private set; } = string.Empty;

    private TextPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => InputBox.Focus();
    }

    public static string? Ask(Window owner, string title, string prompt, string initialValue = "")
    {
        var dlg = new TextPromptWindow
        {
            Owner = owner,
            Title = title,
        };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.Text = initialValue;
        dlg.InputBox.SelectAll();
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel_Click(sender, new RoutedEventArgs());
    }
}
