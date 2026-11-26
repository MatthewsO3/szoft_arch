using System.Windows;
using System.Windows.Controls;

namespace QuizGame.Host.Controls;

public partial class HelpTooltip : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HelpTooltip), new PropertyMetadata(""));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public HelpTooltip() => InitializeComponent();
}
