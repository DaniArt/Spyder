using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;

namespace Spy.UI;

public partial class OverlayWindow : Window
{
    private bool _dpiReady;
    private Matrix _deviceToDip;
    private Point _virtualOriginDip;
    private bool _hasRect;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget is { } ct)
            {
                _deviceToDip = ct.TransformFromDevice;
                var originDevice = new Point(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop);
                _virtualOriginDip = _deviceToDip.Transform(originDevice);
                _dpiReady = true;
            }
        }
        catch
        {
            _dpiReady = false;
        }

        MakeClickThrough();
    }

    /// <summary>
    /// Подсветка по Win32-прямоугольнику (device pixels).
    /// </summary>
    public void ShowRectFromDevice(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) { HideRect(); return; }

        if (_dpiReady)
        {
            var p1 = _deviceToDip.Transform(new Point(x, y));
            var p2 = _deviceToDip.Transform(new Point(x + w, y + h));

            ShowRectInternal(p1.X, p1.Y, p2.X - p1.X, p2.Y - p1.Y);
        }
        else
        {
            ShowRectInternal(x, y, w, h);
        }
    }

    /// <summary>
    /// Подсветка по UIA-прямоугольнику (WPF DIPs, экранные координаты).
    /// </summary>
    public void ShowRectFromWpf(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) { HideRect(); return; }

        ShowRectInternal(x, y, w, h);
    }

    void ShowRectInternal(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) { HideRect(); return; }

        Rect.Visibility = Visibility.Visible;

        var origin = _dpiReady ? _virtualOriginDip : new Point(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop);

        var tx = x - origin.X;
        var ty = y - origin.Y;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (!_hasRect)
        {
            Canvas.SetLeft(Rect, tx);
            Canvas.SetTop(Rect, ty);
            Rect.Width = w;
            Rect.Height = h;
            _hasRect = true;
            return;
        }

        Rect.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(tx, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
        Rect.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(ty, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
        Rect.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
        Rect.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing });
    }

    public void HideRect()
    {
        Rect.Visibility = Visibility.Collapsed;
        _hasRect = false;
    }
	
	public void FadeIn()
	{
		Opacity = 0;
		Show();
		var a = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120));
		BeginAnimation(Window.OpacityProperty, a);
	}

	public void FadeOut()
	{
		var a = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
		a.Completed += (_, _) => Hide();
		BeginAnimation(Window.OpacityProperty, a);
	}

    void MakeClickThrough()
    {
        // чтобы overlay не перехватывал клики
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowExTransparent(hwnd);
    }
}
