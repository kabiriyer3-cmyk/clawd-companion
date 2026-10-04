using System;
using System.Collections.Generic;
using System.Text;

namespace ClawdCompanion.Core
{
    public struct RectD
    {
        public double Left, Top, Right, Bottom;

        public RectD(double left, double top, double right, double bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public double Width { get { return Right - Left; } }
        public double Height { get { return Bottom - Top; } }
        public bool Contains(double x, double y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
    }

    /// <summary>Something Clawd can stand on: the top of a monitor's taskbar/work area, or the top edge of an app window.</summary>
    public sealed class Surface
    {
        public IntPtr Hwnd;
        public double Left, Right, Y;
        public bool TaskbarBelow;
        public RectD Monitor;
        public bool IsWindow { get { return Hwnd != IntPtr.Zero; } }
    }

    /// <summary>
    /// The desktop as Clawd sees it, in WPF device independent pixels:
    /// monitors, work areas (taskbar tops are floors) and the title-bar edges of visible windows.
    /// </summary>
    public sealed class DesktopWorld
    {
        sealed class WinRec
        {
            public IntPtr Hwnd;
            public RectD Rect;
            public bool CanStand;
        }

        public double Dpi = 1.0;
        public IntPtr Self = IntPtr.Zero;
        public bool UseWindows = true;
        public RectD Bounds = new RectD(0, 0, 1920, 1080);
        public RectD PrimaryWork = new RectD(0, 0, 1920, 1040);

        readonly List<Surface> floors = new List<Surface>();
        readonly List<RectD> monitors = new List<RectD>();
        readonly List<WinRec> wins = new List<WinRec>();
        readonly Native.EnumWindowsProc enumProc;
        readonly StringBuilder classBuffer = new StringBuilder(256);

        public DesktopWorld()
        {
            enumProc = OnEnumWindow;
        }

        RectD FromPx(System.Drawing.Rectangle r)
        {
            return new RectD(r.Left / Dpi, r.Top / Dpi, r.Right / Dpi, r.Bottom / Dpi);
        }

        RectD FromPx(Native.RECT r)
        {
            return new RectD(r.Left / Dpi, r.Top / Dpi, r.Right / Dpi, r.Bottom / Dpi);
        }

        // ------------------------------------------------------------------ monitors

        public void RefreshScreens()
        {
            floors.Clear();
            monitors.Clear();
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                RectD mb = FromPx(screen.Bounds);
                RectD wa = FromPx(screen.WorkingArea);
                monitors.Add(mb);
                floors.Add(new Surface
                {
                    Left = wa.Left,
                    Right = wa.Right,
                    Y = wa.Bottom,
                    TaskbarBelow = wa.Bottom < mb.Bottom - 1,
                    Monitor = mb
                });
                if (screen.Primary) PrimaryWork = wa;
                l = Math.Min(l, mb.Left); t = Math.Min(t, mb.Top);
                r = Math.Max(r, mb.Right); b = Math.Max(b, mb.Bottom);
            }
            if (monitors.Count == 0)
            {
                var fallback = new RectD(0, 0, 1920, 1080);
                monitors.Add(fallback);
                floors.Add(new Surface { Left = 0, Right = 1920, Y = 1040, TaskbarBelow = true, Monitor = fallback });
                l = 0; t = 0; r = 1920; b = 1080;
            }
            Bounds = new RectD(l, t, r, b);
        }

        /// <summary>A monitor's bottom edge is not a floor where another monitor continues directly below it (unless a taskbar is there).</summary>
        bool FloorValid(Surface f, double x)
        {
            if (f.TaskbarBelow) return true;
            foreach (var m in monitors)
            {
                if (Math.Abs(m.Top - f.Monitor.Bottom) < 2 && x >= m.Left && x < m.Right) return false;
            }
            return true;
        }

        public RectD MonitorAt(double x, double y)
        {
            foreach (var m in monitors) if (m.Contains(x, y)) return m;
            double best = double.MaxValue; RectD pick = monitors.Count > 0 ? monitors[0] : Bounds;
            foreach (var m in monitors)
            {
                double cx = M.Clamp(x, m.Left, m.Right), cy = M.Clamp(y, m.Top, m.Bottom);
                double d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (d < best) { best = d; pick = m; }
            }
            return pick;
        }

        // ------------------------------------------------------------------ windows

        public void RefreshWindows()
        {
            wins.Clear();
            if (!UseWindows) return;
            try { Native.EnumWindows(enumProc, IntPtr.Zero); }
            catch (Exception ex) { Settings.Log("EnumWindows: " + ex.Message); }
        }

        bool OnEnumWindow(IntPtr h, IntPtr lParam)
        {
            if (h == Self) return true;
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (Native.IsCloaked(h)) return true;

            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true; // click-through overlays

            Native.RECT r;
            if (!Native.TryGetFrame(h, out r)) return true;
            RectD rect = FromPx(r);
            if (rect.Width < 2 || rect.Height < 2) return true;

            string cls = Native.ClassOf(h, classBuffer);
            if (cls == "Progman" || cls == "WorkerW") return true; // the desktop itself

            int style = Native.GetWindowLong(h, Native.GWL_STYLE);
            bool tool = (ex & Native.WS_EX_TOOLWINDOW) != 0;
            bool framed = (style & (Native.WS_CAPTION | Native.WS_THICKFRAME)) != 0;
            bool child = (style & Native.WS_CHILD) != 0;
            bool titled = Native.GetWindowTextLength(h) > 0;
            bool maximized = Native.IsZoomed(h);

            bool fullscreen = false;
            foreach (var m in monitors)
            {
                if (Math.Abs(rect.Left - m.Left) < 2 && Math.Abs(rect.Top - m.Top) < 2 &&
                    Math.Abs(rect.Right - m.Right) < 2 && Math.Abs(rect.Bottom - m.Bottom) < 2) { fullscreen = true; break; }
            }

            bool canStand = !tool && !child && framed && titled && !maximized && !fullscreen &&
                            rect.Width >= 160 && rect.Height >= 80 && cls != "Shell_TrayWnd";

            wins.Add(new WinRec { Hwnd = h, Rect = rect, CanStand = canStand });
            return true;
        }

        bool Occluded(int index, double x, double y)
        {
            for (int j = 0; j < index && j < wins.Count; j++)
            {
                if (wins[j].Rect.Contains(x, y)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ queries

        /// <summary>
        /// Surface Clawd's feet hit while moving from yFrom down to yTo at x.
        /// Window tops count only when actually crossed; monitor floors also catch him if he's already below them.
        /// </summary>
        public Surface FindLanding(double x, double yFrom, double yTo, double centerY)
        {
            Surface best = null;
            double bestY = double.MaxValue;

            if (UseWindows)
            {
                for (int i = 0; i < wins.Count; i++)
                {
                    var w = wins[i];
                    if (!w.CanStand) continue;
                    double y = w.Rect.Top;
                    if (y < yFrom || y > yTo) continue;
                    if (x < w.Rect.Left + 8 || x > w.Rect.Right - 8) continue;
                    if (y < Bounds.Top + 10) continue;
                    if (Occluded(i, x, y + 3)) continue;
                    if (y < bestY)
                    {
                        bestY = y;
                        best = new Surface { Hwnd = w.Hwnd, Left = w.Rect.Left, Right = w.Rect.Right, Y = y };
                    }
                }
            }

            foreach (var f in floors)
            {
                if (x < f.Left || x > f.Right || !FloorValid(f, x)) continue;
                bool crossed = f.Y >= yFrom - 1 && f.Y <= yTo;
                bool alreadyBelow = yTo > f.Y && f.Monitor.Contains(x, centerY);
                if ((crossed || alreadyBelow) && f.Y < bestY) { bestY = f.Y; best = f; }
            }
            return best;
        }

        /// <summary>First surface at or below y at x.</summary>
        public Surface FloorAt(double x, double y)
        {
            Surface best = null;
            double bestY = double.MaxValue;

            if (UseWindows)
            {
                for (int i = 0; i < wins.Count; i++)
                {
                    var w = wins[i];
                    if (!w.CanStand) continue;
                    double top = w.Rect.Top;
                    if (top < y - 3) continue;
                    if (x < w.Rect.Left + 8 || x > w.Rect.Right - 8) continue;
                    if (top < Bounds.Top + 10) continue;
                    if (Occluded(i, x, top + 3)) continue;
                    if (top < bestY)
                    {
                        bestY = top;
                        best = new Surface { Hwnd = w.Hwnd, Left = w.Rect.Left, Right = w.Rect.Right, Y = top };
                    }
                }
            }

            foreach (var f in floors)
            {
                if (x < f.Left || x > f.Right || !FloorValid(f, x)) continue;
                if (f.Y >= y - 3 && f.Y < bestY) { bestY = f.Y; best = f; }
            }
            return best;
        }

        /// <summary>Re-reads a window surface. Returns false if the window is gone, hidden, minimized or maximized.</summary>
        public bool TrackWindow(Surface s, out double dx, out double dy)
        {
            dx = 0; dy = 0;
            IntPtr h = s.Hwnd;
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsZoomed(h) || Native.IsCloaked(h)) return false;
            Native.RECT r;
            if (!Native.TryGetFrame(h, out r)) return false;
            RectD rect = FromPx(r);
            dx = rect.Left - s.Left;
            dy = rect.Top - s.Y;
            s.Left = rect.Left;
            s.Right = rect.Right;
            s.Y = rect.Top;
            return true;
        }

        /// <summary>Is the edge Clawd stands on still exposed (not covered by another window)?</summary>
        public bool IsWindowSurfaceVisible(Surface s, double x)
        {
            if (!UseWindows) return false;
            for (int i = 0; i < wins.Count; i++)
            {
                if (wins[i].Hwnd == s.Hwnd) return wins[i].CanStand && !Occluded(i, x, s.Y + 3);
            }
            return false;
        }
    }
}
