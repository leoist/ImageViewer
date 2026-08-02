using System.Windows;
using System.Windows.Media.Animation;

namespace ImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var win = new MainWindow();
        win.WindowState = WindowState.Minimized;
        win.ShowActivated = false;
        win.Loaded += (_, _) =>
        {
            win.ShowActivated = true;
            win.Opacity = 0;
            win.WindowState = WindowState.Normal;
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            win.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        win.Show();
    }
}
