using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CAUCCampusNetwork.Views;

public partial class SpinningRing : UserControl
{
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush),
        typeof(Brush),
        typeof(SpinningRing),
        new PropertyMetadata(Brushes.Gray)
    );

    public static readonly DependencyProperty RingSizeProperty = DependencyProperty.Register(
        nameof(RingSize),
        typeof(double),
        typeof(SpinningRing),
        new PropertyMetadata(14.0)
    );

    private static readonly DoubleAnimation SpinAnimation = CreateSpinAnimation();

    public SpinningRing()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += (_, _) => SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public double RingSize
    {
        get => (double)GetValue(RingSizeProperty);
        set => SetValue(RingSizeProperty, value);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, SpinAnimation);
        }
        else
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private static DoubleAnimation CreateSpinAnimation()
    {
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        anim.Freeze();
        return anim;
    }
}
