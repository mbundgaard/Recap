using System.Windows;
using System.Windows.Controls;

namespace Recap;

public sealed class RenameDialog : Window
{
    private readonly TextBox _textBox;

    public string TitleText => _textBox.Text;

    public RenameDialog(string currentTitle)
    {
        Title = "Rename recording";
        Width = 360;
        Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Meeting name", Margin = new Thickness(0, 0, 0, 6) });

        _textBox = new TextBox { Text = currentTitle, Height = 30, Margin = new Thickness(0, 0, 0, 14) };
        _textBox.SelectAll();
        panel.Children.Add(_textBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Width = 76, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Save", Width = 76, Height = 30, IsDefault = true };
        save.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _textBox.Focus();
    }
}
