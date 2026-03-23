using System.Windows;

namespace Spy.UI;

public partial class AddSelectorWindow : Window
{
    public string DisplayName => TxtName.Text.Trim();
    public string? Description => string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text.Trim();

    public AddSelectorWindow(string selector)
    {
        InitializeComponent();
        TxtSelector.Text = selector;
        TxtName.Focus();
        TxtName.SelectAll();
    }

    void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            MessageBox.Show("Display name is required.", "Add selector", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

