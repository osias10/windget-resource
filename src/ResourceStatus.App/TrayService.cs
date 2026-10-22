using System;
using System.Drawing;
using System.Windows;
using ResourceStatus.Core;
using Forms = System.Windows.Forms;

namespace ResourceStatus.App;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Action _refreshMenu;
    internal Forms.ContextMenuStrip Menu => _menu;
    internal void RefreshMenu() => _refreshMenu();
    public TrayService(MainWindow window, AppController controller, Action exit)
    {
        _icon = new Forms.NotifyIcon { Text = "Resource Status · 시스템 상태", Icon = SystemIcons.Application, ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Application.Current.Dispatcher.Invoke(window.ShowWidget); };
        _refreshMenu = () =>
        {
            while (_menu.Items.Count > 0) _menu.Items[0].Dispose();
            Add("위젯 열기", window.ShowWidget);
            var modes = new Forms.ToolStripMenuItem("위젯 크기");
            foreach (var mode in Enum.GetValues<WidgetMode>())
            {
                var item = new Forms.ToolStripMenuItem(mode == WidgetMode.Compact ? "작게" : "크게") { Checked = controller.Settings.WidgetMode == mode };
                item.Click += (_, _) => Application.Current.Dispatcher.Invoke(() => { window.SetMode(mode); window.ShowWidget(); });
                modes.DropDownItems.Add(item);
            }
            _menu.Items.Add(modes);
            AddToggle("위젯 크기 고정", controller.Settings.LockWidgetSize, value => controller.Settings.LockWidgetSize = value);
            AddToggle("클릭 통과", controller.Settings.ClickThrough, value => controller.Settings.ClickThrough = value);
            Add("설정", window.ShowSettings);
            Add("업데이트 확인", controller.RequestUpdate);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            Add("종료", exit);
        };
        _menu.Opening += (_, _) => RefreshMenu();
        void Add(string text, Action action) => _menu.Items.Add(text, null, (_, _) => Application.Current.Dispatcher.Invoke(action));
        void AddToggle(string text, bool enabled, Action<bool> change)
        {
            var item = new Forms.ToolStripMenuItem(text) { Checked = enabled, CheckOnClick = true };
            item.Click += (_, _) => Application.Current.Dispatcher.Invoke(() => { change(item.Checked); controller.Save(); });
            _menu.Items.Add(item);
        }
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
