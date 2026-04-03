using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;

namespace HDT_BGTracker
{
    public class BGTrackerPlugin : IPlugin
    {
        public string Name => "BG Rating Tracker";
        public string Description => "酒馆战棋结束后自动记录分数并上传到 MongoDB";
        public string Author => "BGTracker";
        public Version Version => new Version(1, 0, 0);
        public string ButtonText => "测试连接";

        public MenuItem MenuItem { get; private set; }

        private RatingTracker _tracker;
        private LobbyOverlay _overlay;

        public void OnLoad()
        {
            _tracker = new RatingTracker();
            _overlay = new LobbyOverlay();
            _tracker.SetOverlay(_overlay);

            // 将 overlay 添加到 HDT 的覆盖层
            Core.OverlayWindow.Children.Add(_overlay);

            _tracker.Start();
            CreateMenuItem();
        }

        public void OnUnload()
        {
            _tracker?.Stop();
            _tracker = null;

            if (_overlay != null)
            {
                Core.OverlayWindow.Children.Remove(_overlay);
                _overlay = null;
            }
        }

        public void OnUpdate()
        {
            _tracker?.OnUpdate();
        }

        public void OnButtonPress()
        {
            _tracker?.TestConnection();
        }

        private void CreateMenuItem()
        {
            MenuItem = new MenuItem
            {
                Header = "BG Rating Tracker",
                IsCheckable = true,
                IsChecked = true
            };

            MenuItem.Checked += (s, e) => _tracker?.Start();
            MenuItem.Unchecked += (s, e) => _tracker?.Stop();
        }
    }
}
