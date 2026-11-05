using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DailyPlan.App;

/// <summary>
/// 主窗口隐藏到托盘时，用于展示任务提醒的右下角置顶弹窗，视觉与主界面 Toast 保持一致。
/// 复用单个实例：连续提醒会替换内容并重新计时。
/// </summary>
public sealed class NotificationPopup : Window
{
    private static readonly TimeSpan ShowDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(350);

    private readonly TextBlock _titleText;
    private readonly TextBlock _messageText;
    private readonly DispatcherTimer _autoCloseTimer;
    private bool _fadingOut;

    public NotificationPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        MaxWidth = 520;

        if (Application.Current.Resources["AppFontFamily"] is FontFamily appFont)
        {
            FontFamily = appFont;
        }

        _titleText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _messageText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Brushes.White,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };

        var textStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        textStack.Children.Add(_titleText);
        textStack.Children.Add(_messageText);

        var icon = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "\uEA8F",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(textStack);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x22, 0x33)),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(18, 11, 18, 11),
            Child = panel,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 5,
                Opacity = 0.28,
                Color = Color.FromRgb(0x10, 0x13, 0x22)
            }
        };

        Content = border;

        MouseLeftButtonDown += (_, _) => BeginFadeOut();

        _autoCloseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ShowDuration
        };
        _autoCloseTimer.Tick += (_, _) => BeginFadeOut();
    }

    /// <summary>显示提醒；若已可见则替换内容并重新计时。</summary>
    public void Show(string title, string message)
    {
        _titleText.Text = title;
        _messageText.Text = message;

        _autoCloseTimer.Stop();
        _fadingOut = false;

        if (IsVisible)
        {
            _autoCloseTimer.Start();
            return;
        }

        Opacity = 0;
        Show();
        PositionAtBottomRight();
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, FadeInDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        _autoCloseTimer.Start();
    }

    private void PositionAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void BeginFadeOut()
    {
        if (_fadingOut || !IsVisible)
        {
            return;
        }

        _fadingOut = true;
        _autoCloseTimer.Stop();
        var animation = new DoubleAnimation(0, FadeOutDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, animation);
    }
}
