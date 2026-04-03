using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Utility.Extensions;

namespace HDT_BGTracker
{
    public class LobbyOverlay : UserControl
    {
        private readonly TextBlock _textBlock;
        private readonly Grid _rootGrid;
        private bool _isDragging;
        private Point _originalMousePosition;
        private Point _originalGridPosition;

        public LobbyOverlay()
        {
            // 构建 UI
            _rootGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            _textBlock = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE3, 0xE3)),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x27, 0x24, 0x24)),
                FontSize = 18,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            _textBlock.MouseLeftButtonDown += OnMouseLeftButtonDown;
            _textBlock.MouseLeftButtonUp += OnMouseLeftButtonUp;
            _textBlock.MouseMove += OnMouseMove;

            OverlayExtensions.SetIsOverlayHitTestVisible(_textBlock, true);

            _rootGrid.Children.Add(_textBlock);
            Content = _rootGrid;

            Visibility = Visibility.Hidden;

            // 添加到 HDT 覆盖层
            Core.OverlayCanvas.Children.Add(this);
        }

        public void DisplayResult(string text)
        {
            _textBlock.Text = text;
            Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Visibility = Visibility.Hidden;
        }

        public void Cleanup()
        {
            Core.OverlayCanvas.Children.Remove(this);
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _originalMousePosition = e.GetPosition(this);
            _originalGridPosition = new Point(_rootGrid.Margin.Left, _rootGrid.Margin.Top);
            _textBlock.CaptureMouse();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _textBlock.ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var currentPos = e.GetPosition(this);
            double offsetX = currentPos.X - _originalMousePosition.X;
            double offsetY = currentPos.Y - _originalMousePosition.Y;
            double newLeft = Math.Max(0, _originalGridPosition.X + offsetX);
            double newTop = Math.Max(0, _originalGridPosition.Y + offsetY);
            _rootGrid.Margin = new Thickness(newLeft, newTop, 0, 0);
        }
    }
}
