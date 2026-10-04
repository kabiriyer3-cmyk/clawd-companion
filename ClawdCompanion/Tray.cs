using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using ClawdCompanion.Core;

namespace ClawdCompanion
{
    /// <summary>Notification-area icon. Left click wakes / tucks Clawd away, right click opens the Codex menu.</summary>
    sealed class TrayIcon : IDisposable
    {
        readonly System.Windows.Forms.NotifyIcon icon = new System.Windows.Forms.NotifyIcon();

        public event Action LeftClick;
        public event Action RightClick;

        public TrayIcon()
        {
            try
            {
                using (Stream s = typeof(TrayIcon).Assembly.GetManifestResourceStream("ClawdCompanion.clawd.ico"))
                {
                    if (s != null) icon.Icon = new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
                }
            }
            catch (Exception ex) { Settings.Log("Tray icon: " + ex.Message); }
            if (icon.Icon == null) icon.Icon = System.Drawing.SystemIcons.Application;
            icon.Text = "Clawd";
            icon.MouseUp += OnMouseUp;
            icon.Visible = true;
        }

        void OnMouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                if (RightClick != null) RightClick();
            }
            else if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if (LeftClick != null) LeftClick();
            }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }

    /// <summary>
    /// Clawd's own window never takes focus (so it doesn't steal your typing). Menus need a foreground
    /// window to close properly when you click elsewhere, so they open from this tiny off-screen host.
    /// </summary>
    sealed class MenuHost
    {
        Window host;

        void Ensure()
        {
            if (host != null) return;
            host = new Window
            {
                Width = 1,
                Height = 1,
                Left = -20000,
                Top = -20000,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Title = "Clawd menu"
            };
            host.SourceInitialized += (s, e) =>
            {
                IntPtr h = new WindowInteropHelper(host).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
            };
            host.Show();
        }

        public void Show(ContextMenu menu)
        {
            Ensure();
            host.Activate();
            menu.PlacementTarget = host;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
    }
}
