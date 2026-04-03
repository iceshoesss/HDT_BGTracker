using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HDT_BGTracker
{
    public partial class LobbyOverlay : UserControl
    {
        private bool _isDragging;
        private Point _startPoint;

        public LobbyOverlay()
        {
            InitializeComponent();
            Visibility = Visibility.Hidden;
        }

        public void DisplayResult(string text)
        {
            LobbyText.Text = text;
            Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Visibility = Visibility.Hidden;
        }

        private void LobbyText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(this);
            LobbyText.CaptureMouse();
        }

        private void LobbyText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            LobbyText.ReleaseMouseCapture();
        }

        private void LobbyText_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var currentPos = e.GetPosition(Application.Current.MainWindow);
            var offset = currentPos - _startPoint;
            Margin = new Thickness(offset.X, offset.Y, 0, 0);
        }
    }
}
