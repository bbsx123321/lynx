using System.Windows;
using PadShift.App.Services;

namespace PadShift.App.Views;

/// <summary>
/// Modal confirmation dialog used by <see cref="ConfirmationService"/>. The dialog owns no policy:
/// the caller decides whether a suppressed key skips the question.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(ConfirmationRequest request)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(request);

        Title = string.IsNullOrWhiteSpace(request.Title) ? "确认" : request.Title;
        TitleText.Text = Title;
        MessageText.Text = request.Message ?? "";
        OkButton.Content = string.IsNullOrWhiteSpace(request.ActionText) ? "确定" : request.ActionText;
        DoNotAskCheck.Visibility = string.IsNullOrWhiteSpace(request.Key)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>True when the user asked to suppress this question in the future.</summary>
    public bool DoNotAskAgain => DoNotAskCheck.IsChecked == true;

    /// <summary>Shows the dialog modally; a missing owner (shutdown, tray-only) is tolerated.</summary>
    public static ConfirmationAnswer Prompt(Window? owner, ConfirmationRequest request)
    {
        var dialog = new ConfirmDialog(request);
        try
        {
            if (owner is not null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }
            else if (Application.Current?.MainWindow is { IsVisible: true } main)
            {
                dialog.Owner = main;
            }
        }
        catch (InvalidOperationException)
        {
            // The owner window was closed between the checks; show the dialog without one.
        }

        bool? result = dialog.ShowDialog();
        return new ConfirmationAnswer(result == true, dialog.DoNotAskAgain);
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
