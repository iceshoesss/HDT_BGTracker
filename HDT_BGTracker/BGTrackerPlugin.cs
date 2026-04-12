using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;

namespace HDT_BGTracker
{
    public class BGTrackerPlugin : IPlugin
    {
        public string Name => "小群战棋记录";
        public string Description => "在有限小群体内记录酒馆战棋对战数据";
        public string Author => "iceshoes";
        public Version Version => new Version(1, 0, 0);
        public string ButtonText => "测试连接";

        public MenuItem MenuItem { get; private set; }

        private RatingTracker _tracker;
        // private LobbyOverlay _overlay; // 浮动窗口已禁用

        public void OnLoad()
        {
            _tracker = new RatingTracker();
            // _overlay = new LobbyOverlay();
            // _tracker.SetOverlay(_overlay);
            _tracker.Start();
            CreateMenuItem();
        }

        public void OnUnload()
        {
            _tracker?.Stop();
            _tracker = null;

            // _overlay?.Cleanup();
            // _overlay = null;
        }

        public void OnUpdate()
        {
            _tracker?.OnUpdate();
        }

        public void OnButtonPress()
        {
            _tracker?.TestConnection();
            RatingTracker.TestHearthDb();
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
