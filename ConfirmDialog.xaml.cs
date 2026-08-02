using System.Windows;
using System.Windows.Input;

namespace ImageViewer;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog(string message, string? fileName = null)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        MessageText.Text = fileName != null
            ? $"{message}\n{fileName}" : message;
        Loaded += (_, _) =>
        {
            CancelButton.Focus();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Confirmed = false; Close(); }
                if (e.Key == Key.Enter) { Confirmed = true; Close(); }
            };
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }
}
