using System.Windows;
using System.Windows.Controls;
using CAUCCampusNetwork.ViewModels;

namespace CAUCCampusNetwork.Views;

public partial class StackStatusCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(StackStatusCard),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(StackCardState),
        typeof(StackStatusCard),
        new PropertyMetadata(StackCardState.Unknown)
    );

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(StackStatusCard),
        new PropertyMetadata(string.Empty)
    );

    public StackStatusCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public StackCardState State
    {
        get => (StackCardState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
}
