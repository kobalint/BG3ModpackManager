using System.Windows;
using BG3ModpackManager.App.Services;

namespace BG3ModpackManager.App.Views.Dialogs;

public partial class TextPromptDialog : Window
{
    public string InputText { get; private set; } = string.Empty;
    public bool OptionChecked { get; private set; }
    public bool Option2Checked { get; private set; }

    public TextPromptDialog(
        string title,
        string description,
        string initialValue = "",
        string? checkbox1Text = null,
        bool checkbox1Default = false,
        string? checkbox2Text = null,
        bool checkbox2Default = false)
    {
        InitializeComponent();
        Title = title;
        PromptHeading.Text = title;
        PromptDescription.Text = description;
        InputBox.Text = initialValue;

        if (!string.IsNullOrWhiteSpace(checkbox1Text))
        {
            OptionCheckBox.Content = checkbox1Text;
            OptionCheckBox.IsChecked = checkbox1Default;
            OptionCheckBox.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(checkbox2Text))
        {
            OptionCheckBox2.Content = checkbox2Text;
            OptionCheckBox2.IsChecked = checkbox2Default;
            OptionCheckBox2.Visibility = Visibility.Visible;
        }

        InputText = initialValue.Trim();
        OptionChecked = checkbox1Default && !string.IsNullOrWhiteSpace(checkbox1Text);
        Option2Checked = checkbox2Default && !string.IsNullOrWhiteSpace(checkbox2Text);

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            MessageBox.Show(this, Loc.T("Dialog.TextPrompt.EmptyValMsg"), Loc.T("Common.ValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InputText = InputBox.Text.Trim();
        OptionChecked = OptionCheckBox.IsChecked == true;
        Option2Checked = OptionCheckBox2.IsChecked == true;
        DialogResult = true;
    }
}
