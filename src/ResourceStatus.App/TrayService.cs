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
    public TrayService(MainWindow window, AppController controller, Action exit)
    {
        _icon = new Forms.NotifyIcon { Text = "Resource Status · 시스템 상태", Icon = SystemIcons.Application, ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Application.Current.Dispatcher.Invoke(window.ShowWidget); };
        _menu.Opening += (_, _) =>
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
            Add("설정", window.ShowSettings);
            Add("업데이트 확인", controller.RequestUpdate);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            Add("종료", exit);
        };
        void Add(string text, Action action) => _menu.Items.Add(text, null, (_, _) => Application.Current.Dispatcher.Invoke(action));
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
