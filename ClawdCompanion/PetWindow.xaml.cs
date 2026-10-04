using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using ClawdCompanion.Core;
using ClawdCompanion.Render;
using ClawdCompanion.Sim;

namespace ClawdCompanion
{
    /// <summary>
    /// A small transparent, always-on-top, non-activating window that follows Clawd around the desktop.
    /// Transparent pixels are click-through, so only Clawd, his ring and the tray catch the mouse.
    /// </summary>
    public partial class PetWindow : Window
    {
        const double AnchorFromTop = 1.75;  // body centre sits this many model units below the window top
        const double WidthUnits = 2.8, HeightUnits = 3.1;
        const int HotkeyId = 0xC1A;

        sealed class Particle
        {
            public FrameworkElement El;
            public ScaleTransform Scale;
            public RotateTransform Rot;
            public FxKind Kind;
            public V2 Pos, Vel;
            public double Age, Life, Seed;
        }

        readonly Settings settings;
        readonly PetPhysics ph = new PetPhysics();
        readonly DesktopWorld world = new DesktopWorld();
        readonly PetBrain brain;
        readonly ClawdRig rig;
        readonly Random rnd = new Random();

        IntPtr hwnd = IntPtr.Zero;
        double dpi = 1;
        double S = 140, W = 392, H = 434;
        double winLeft, winTop;
        int lastPx = int.MinValue, lastPy = int.MinValue;

        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTime, now;
        bool running, awake;

        bool dragging, bodyDown, bodyDragHinted;
        V2 bodyDownPos, cursor, prevCursor;
        double cursorSpeed, cursorStill;
        bool cursorInit;

        double lastHover = -10, pillAlpha, pillX, pillY;
        bool pillPlaced;
        string shownText = "";
        Color shownColor;

        double tWindows, tScreens, tTopmost, tSave = 30;
        readonly List<Particle> particles = new List<Particle>();
        readonly Dictionary<Color, SolidColorBrush> brushCache = new Dictionary<Color, SolidColorBrush>();
        static readonly Dictionary<FxKind, Geometry> glyphCache = new Dictionary<FxKind, Geometry>();

        public event Action MenuRequested;
        public event Action HotkeyPressed;

        public PetBrain Brain { get { return brain; } }
        public bool IsAwake { get { return awake; } }

        public PetWindow(Settings settings)
        {
            InitializeComponent();
            this.settings = settings;
            ph.World = world;
            brain = new PetBrain(ph, settings, world);

            Dictionary<string, ObjPart> parts;
            using (System.IO.Stream s = typeof(PetWindow).Assembly.GetManifestResourceStream("ClawdCompanion.clawd_rig.obj"))
            {
                if (s == null) throw new InvalidOperationException("Embedded Clawd model not found.");
                parts = ObjLoader.Load(s);
            }
            rig = new ClawdRig(parts);
            LightsVisual.Content = ClawdRig.CreateLights();
            View.Children.Add(rig.Visual);

            View.MouseLeftButtonDown += View_MouseLeftButtonDown;
            View.MouseLeftButtonUp += View_MouseLeftButtonUp;
            View.LostMouseCapture += (o, e) => { bodyDown = false; };
            MouseRightButtonUp += (o, e) =>
            {
                e.Handled = true;
                if (MenuRequested != null) MenuRequested();
            };

            SourceInitialized += OnSourceInitialized;
            Closed += OnClosedWindow;
        }

        // ------------------------------------------------------------------ window plumbing

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            world.Self = hwnd;

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            ex &= ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);

            HwndSource src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);
            Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)'C');

            dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (dpi <= 0) dpi = 1;
            world.Dpi = dpi;
            world.RefreshScreens();
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
            {
                handled = true;
                if (HotkeyPressed != null) HotkeyPressed();
            }
            return IntPtr.Zero;
        }

        void OnClosedWindow(object sender, EventArgs e)
        {
            Stop();
            if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, HotkeyId);
        }

        public void ApplySettings()
        {
            S = settings.Scale;
            W = Math.Round(WidthUnits * S);
            H = Math.Round(HeightUnits * S);
            Width = W;
            Height = H;
            Cam.Width = W / S;
            ph.S = S;
            world.UseWindows = settings.SitOnWindows;
            if (!settings.SitOnWindows && ph.Support != null && ph.Support.IsWindow) ph.DropSupport();
            RingHandle.Width = RingHandle.Height = Math.Round(0.40 * S);
            lastPx = int.MinValue;
            if (awake) ph.ResetChain();
        }

        public void Wake()
        {
            if (awake) return;
            awake = true;
            ApplySettings();
            if (!IsVisible) Show();
            if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(this).Handle;
            world.RefreshScreens();
            world.RefreshWindows();

            double x = settings.LastX ?? (world.PrimaryWork.Right - 1.6 * S);
            if (x < world.Bounds.Left + S || x > world.Bounds.Right - S) x = world.PrimaryWork.Right - 1.6 * S;
            RectD mon = world.MonitorAt(x, world.PrimaryWork.Top + 10);
            ph.SpawnAt(x, mon.Top + 0.6 * S); // drops in from the top of the screen
            brain.OnWake();
            pillAlpha = 0;
            Start();
        }

        public void TuckAway()
        {
            if (!awake) return;
            EndDrag();
            awake = false;
            settings.LastX = ph.C.X;
            settings.Save();
            Stop();
            ClearParticles();
            Hide();
        }

        public void SaveState()
        {
            if (awake) settings.LastX = ph.C.X;
            settings.Save();
        }

        void Start()
        {
            if (running) return;
            running = true;
            lastTime = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        void Stop()
        {
            if (!running) return;
            running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        // ------------------------------------------------------------------ frame loop

        void OnFrame(object sender, EventArgs e)
        {
            now = clock.Elapsed.TotalSeconds;
            double dt = now - lastTime;
            if (dt < 0.001) return;
            lastTime = now;
            if (dt > 0.05) dt = 0.05;
            try { Tick(dt); }
            catch (Exception ex) { Settings.Log("Frame: " + ex); }
        }

        void Tick(double dt)
        {
            ReadCursor(dt);

            tScreens -= dt;
            if (tScreens <= 0) { tScreens = 3; world.RefreshScreens(); }

            tWindows -= dt;
            if (tWindows <= 0)
            {
                tWindows = 0.15;
                world.RefreshWindows();
                if (ph.Mode == PetMode.Grounded && ph.Support != null && ph.Support.IsWindow &&
                    !world.IsWindowSurfaceVisible(ph.Support, ph.C.X))
                    ph.DropSupport();
            }

            tTopmost -= dt;
            if (tTopmost <= 0)
            {
                tTopmost = 2;
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }

            tSave -= dt;
            if (tSave <= 0) { tSave = 30; SaveState(); }

            if (dragging && !Native.IsPrimaryButtonDown()) EndDrag();
            if (bodyDown && !bodyDragHinted && (cursor - bodyDownPos).Length > 10)
            {
                bodyDragHinted = true;
                brain.OnBodyDragAttempt();
            }

            // hover + petting
            bool overBody = IsOverBody(cursor);
            bool overRing = (cursor - ph.RingCenter).Length < 0.21 * S;
            bool overPill = pillAlpha > 0.3 && Pill.ActualWidth > 0 &&
                            cursor.X >= winLeft + pillX - 4 && cursor.X <= winLeft + pillX + Pill.ActualWidth + 4 &&
                            cursor.Y >= winTop + pillY - 8 && cursor.Y <= winTop + pillY + Pill.ActualHeight + 8;
            if ((overBody || overRing || overPill) && !dragging) lastHover = now;
            if (overBody && !dragging && !bodyDown && ph.Mode == PetMode.Grounded)
            {
                double stroke = (cursor - prevCursor).Length;
                if (stroke > 0.5 && stroke < 200) brain.OnPetStroke(stroke);
            }

            var info = new CursorInfo
            {
                Pos = cursor,
                Speed = cursorSpeed,
                Dist = (cursor - ph.C).Length,
                StillTime = cursorStill,
                OverBody = overBody
            };

            brain.Think(dt, info);
            ph.TrackSupport();
            ph.Step(dt, cursor);
            brain.Animate(dt);

            PlaceWindow();
            ApplyPose();
            UpdateOverlay(dt);
        }

        void ReadCursor(double dt)
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            V2 c = new V2(p.X / dpi, p.Y / dpi);
            if (!cursorInit) { cursor = c; prevCursor = c; cursorInit = true; }
            prevCursor = cursor;
            cursor = c;
            double moved = (cursor - prevCursor).Length;
            cursorSpeed = M.Damp(cursorSpeed, moved / Math.Max(dt, 1e-3), 12, dt);
            if (moved > 1.5) cursorStill = 0; else cursorStill += dt;
        }

        V2 CursorNow()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            return new V2(p.X / dpi, p.Y / dpi);
        }

        bool IsOverBody(V2 p)
        {
            V2 local = (p - ph.C).Rotate(-ph.Theta);
            double lx = local.X / S, ly = -local.Y / S + PetPhysics.ComY;
            return Math.Abs(lx) < 0.6 && ly > 0.0 && ly < 0.84;
        }

        void PlaceWindow()
        {
            double left = ph.C.X - W / 2, top = ph.C.Y - AnchorFromTop * S;
            int px = (int)Math.Round(left * dpi), py = (int)Math.Round(top * dpi);
            if (px != lastPx || py != lastPy)
            {
                Native.SetWindowPos(hwnd, IntPtr.Zero, px, py, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                lastPx = px;
                lastPy = py;
            }
            winLeft = px / dpi;
            winTop = py / dpi;
        }

        Point3D ToScene(V2 p)
        {
            return new Point3D((p.X - winLeft - W / 2) / S, -(p.Y - winTop - H / 2) / S, 0);
        }

        void ApplyPose()
        {
            PetPose pose = brain.Pose;
            rig.SetBody(ToScene(ph.PivotWorld), ph.PivotY, ph.Sx, ph.Sy, pose.YawDeg, -M.Deg(ph.Theta));
            V2 d2 = ph.P2 - ph.P1;
            rig.SetJumpRing(ToScene(ph.P1), M.Deg(Math.Atan2(d2.X, d2.Y)), pose.JumpTwistDeg);
            V2 d1 = ph.P1 - ph.P0;
            rig.SetKeyRing(ToScene(ph.P0), M.Deg(Math.Atan2(d1.X, d1.Y)), pose.KeyTwistDeg);
            rig.SetFace(pose);
            rig.SetLimbs(pose.LegSwing, pose.LegLift, pose.ArmL, pose.ArmR);
        }

        // ------------------------------------------------------------------ overlay: ring handle, shadow, tray, particles

        SolidColorBrush BrushFor(Color c)
        {
            SolidColorBrush b;
            if (!brushCache.TryGetValue(c, out b))
            {
                b = new SolidColorBrush(c);
                b.Freeze();
                brushCache[c] = b;
            }
            return b;
        }

        void UpdateOverlay(double dt)
        {
            V2 rc = ph.RingCenter;
            double rr = RingHandle.Width / 2;
            Canvas.SetLeft(RingHandle, rc.X - winLeft - rr);
            Canvas.SetTop(RingHandle, rc.Y - winTop - rr);

            UpdateShadow();

            bool expanded = now - lastHover < 0.9 && !dragging && ph.Mode != PetMode.Held;
            string speech = brain.Speech;
            bool show = expanded || speech != null;
            string text = speech ?? brain.Status;
            if (text != shownText) { StatusText.Text = text; shownText = text; }
            Color col = brain.StatusColor;
            if (col != shownColor)
            {
                SolidColorBrush b = BrushFor(col);
                StatusDot.Fill = b;
                StatusHalo.Fill = b;
                shownColor = col;
            }
            PillActions.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            BtnNap.Content = brain.IsSleeping ? "\uE706" : "\uE708";
            BtnNap.ToolTip = brain.IsSleeping ? "Wake up" : "Nap time";

            // Codex-style "running" pulse on the status dot
            double pulse = (now * 0.9) % 1.0;
            HaloScale.ScaleX = HaloScale.ScaleY = 1 + pulse * 1.1;
            StatusHalo.Opacity = 0.45 * (1 - pulse);

            pillAlpha = M.Damp(pillAlpha, show ? 1 : 0, 14, dt);
            Pill.Opacity = pillAlpha;
            Pill.Visibility = pillAlpha < 0.02 ? Visibility.Hidden : Visibility.Visible;
            Pill.IsHitTestVisible = pillAlpha > 0.5;
            if (Pill.Visibility == Visibility.Visible && Pill.ActualWidth > 0)
            {
                double pw = Pill.ActualWidth, phh = Pill.ActualHeight;
                double topMost = Math.Min(Math.Min(ph.P0.Y, rc.Y - 0.15 * S), ph.BodyPoint(0, PetPhysics.BodyTopY).Y);
                double ty = topMost - winTop - 12 - phh;
                if (ty < 4) ty = ph.BodyPoint(0, PetPhysics.FeetY).Y - winTop + 14;
                ty = M.Clamp(ty, 2, H - phh - 2);
                double tx = M.Clamp(ph.C.X - winLeft - pw / 2, 2, W - pw - 2);
                if (!pillPlaced) { pillX = tx; pillY = ty; pillPlaced = true; }
                pillX = M.Damp(pillX, tx, 16, dt);
                pillY = M.Damp(pillY, ty, 16, dt);
                Canvas.SetLeft(Pill, Math.Round(pillX));
                Canvas.SetTop(Pill, Math.Round(pillY));
            }
            else if (pillAlpha < 0.02) pillPlaced = false;

            UpdateParticles(dt);
        }

        void UpdateShadow()
        {
            double feetY = ph.BodyPoint(0, PetPhysics.FeetY).Y;
            Surface floor = ph.Mode == PetMode.Grounded ? ph.Support : world.FloorAt(ph.C.X, feetY - 2);
            if (floor == null) { Shadow.Opacity = 0; return; }
            double height = Math.Max(0, floor.Y - feetY);
            double f = 1 - M.Clamp01(height / (2.4 * S));
            double sw = 0.95 * S * (0.55 + 0.45 * f);
            double sh = 0.13 * S * (0.6 + 0.4 * f);
            double localY = floor.Y - winTop;
            if (localY < 0 || localY > H) { Shadow.Opacity = 0; return; }
            Shadow.Width = sw;
            Shadow.Height = sh;
            Canvas.SetLeft(Shadow, ph.C.X - winLeft - sw / 2);
            Canvas.SetTop(Shadow, localY - sh * 0.62);
            Shadow.Opacity = 0.75 * f;
        }

        static Geometry PixelGeometry(params string[] rows)
        {
            var g = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    if (rows[y][x] == '#') g.Children.Add(new RectangleGeometry(new Rect(x, y, 1.02, 1.02)));
            g.Freeze();
            return g;
        }

        static Geometry GlyphFor(FxKind kind)
        {
            Geometry g;
            if (glyphCache.TryGetValue(kind, out g)) return g;
            switch (kind)
            {
                case FxKind.Z: g = PixelGeometry("#####", "...#.", "..#..", ".#...", "#####"); break;
                case FxKind.Heart: g = PixelGeometry(".##.##.", "#######", "#######", ".#####.", "..###..", "...#..."); break;
                case FxKind.Bang: g = PixelGeometry("##", "##", "##", "##", "..", "##"); break;
                case FxKind.Question: g = PixelGeometry(".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."); break;
                case FxKind.Note: g = PixelGeometry("..###", "..#.#", "..#..", "..#..", "###..", "###.."); break;
                default: g = PixelGeometry("..#..", "..#..", "##.##", "..#..", "..#.."); break;
            }
            glyphCache[kind] = g;
            return g;
        }

        static Color ColorFor(FxKind kind)
        {
            switch (kind)
            {
                case FxKind.Z: return Color.FromRgb(0xE7, 0xE2, 0xFF);
                case FxKind.Heart: return Color.FromRgb(0xFF, 0x5C, 0x7A);
                case FxKind.Bang: return Color.FromRgb(0xFF, 0xB2, 0x24);
                case FxKind.Question: return Palette.Blue;
                case FxKind.Note: return Palette.Purple;
                default: return Color.FromRgb(0xFF, 0xE2, 0x7A);
            }
        }

        void SpawnParticle(FxSpawn fx)
        {
            if (particles.Count > 24) return;
            double k = S / 140.0;
            Geometry geo = GlyphFor(fx.Kind);
            Rect bounds = geo.Bounds;
            double px = 2.3 * k; // DIPs per glyph pixel
            var path = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = BrushFor(ColorFor(fx.Kind)),
                Stretch = Stretch.Fill,
                Width = Math.Max(2, bounds.Width * px),
                Height = Math.Max(2, bounds.Height * px),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(20, 18, 17),
                    BlurRadius = 0,
                    ShadowDepth = 1.6 * k,
                    Direction = 315,
                    Opacity = 0.9
                }
            };
            var scale = new ScaleTransform(0.01, 0.01);
            var rot = new RotateTransform(0);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(rot);
            path.RenderTransform = group;

            var p = new Particle
            {
                El = path,
                Scale = scale,
                Rot = rot,
                Kind = fx.Kind,
                Pos = fx.Pos,
                Vel = fx.Vel,
                Seed = rnd.NextDouble() * 6.28
            };
            switch (fx.Kind)
            {
                case FxKind.Z: p.Life = 2.4; break;
                case FxKind.Sparkle: p.Life = 0.7; break;
                case FxKind.Bang:
                case FxKind.Question: p.Life = 1.0; break;
                default: p.Life = 1.5; break;
            }
            FxLayer.Children.Add(path);
            particles.Add(p);
        }

        void UpdateParticles(double dt)
        {
            foreach (FxSpawn fx in brain.Fx) SpawnParticle(fx);
            brain.Fx.Clear();

            double k = S / 140.0;
            for (int i = particles.Count - 1; i >= 0; i--)
            {
                Particle p = particles[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    FxLayer.Children.Remove(p.El);
                    particles.RemoveAt(i);
                    continue;
                }
                double u = p.Age / p.Life;
                double scale = 1, alpha = 1, wob = 0;
                switch (p.Kind)
                {
                    case FxKind.Z:
                        scale = 0.6 + 0.7 * u;
                        wob = Math.Sin(p.Age * 3 + p.Seed) * 6 * k;
                        alpha = Math.Min(1, p.Age / 0.2) * (1 - M.Clamp01((u - 0.6) / 0.4));
                        break;
                    case FxKind.Heart:
                    case FxKind.Note:
                        scale = p.Age < 0.18 ? p.Age / 0.18 * 1.15 : 1.15 - 0.15 * M.Clamp01((p.Age - 0.18) / 0.15);
                        wob = Math.Sin(p.Age * 5 + p.Seed) * 5 * k;
                        alpha = 1 - M.Clamp01((u - 0.55) / 0.45);
                        break;
                    case FxKind.Bang:
                    case FxKind.Question:
                        scale = p.Age < 0.12 ? p.Age / 0.12 * 1.3 : 1.3 - 0.3 * M.Clamp01((p.Age - 0.12) / 0.12);
                        alpha = 1 - M.Clamp01((u - 0.7) / 0.3);
                        break;
                    default:
                        p.Vel = p.Vel * Math.Exp(-3 * dt);
                        scale = 1 - u;
                        p.Rot.Angle = 90 * u;
                        alpha = 1 - u * 0.5;
                        break;
                }
                p.Pos = p.Pos + p.Vel * dt;
                p.Scale.ScaleX = Math.Max(0.01, scale);
                p.Scale.ScaleY = Math.Max(0.01, scale);
                p.El.Opacity = alpha;
                Canvas.SetLeft(p.El, p.Pos.X + wob - winLeft - p.El.Width / 2);
                Canvas.SetTop(p.El, p.Pos.Y - winTop - p.El.Height / 2);
            }
        }

        void ClearParticles()
        {
            foreach (var p in particles) FxLayer.Children.Remove(p.El);
            particles.Clear();
            brain.Fx.Clear();
        }

        // ------------------------------------------------------------------ input

        void RingHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (dragging) return;
            dragging = true;
            RingHandle.CaptureMouse();
            cursor = CursorNow();
            ph.Grab(cursor);
            brain.OnGrab();
        }

        void RingHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            EndDrag();
        }

        void RingHandle_LostMouseCapture(object sender, MouseEventArgs e)
        {
            EndDrag();
        }

        void EndDrag()
        {
            if (!dragging) return;
            dragging = false;
            if (RingHandle.IsMouseCaptured) RingHandle.ReleaseMouseCapture();
            ph.Release();
        }

        void View_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            cursor = CursorNow();
            if (e.ClickCount >= 2)
            {
                bodyDown = false;
                brain.OnDoubleClick();
                return;
            }
            bodyDown = true;
            bodyDragHinted = false;
            bodyDownPos = cursor;
            View.CaptureMouse();
        }

        void View_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!bodyDown) return;
            e.Handled = true;
            bodyDown = false;
            if (View.IsMouseCaptured) View.ReleaseMouseCapture();
            if (!bodyDragHinted) brain.OnPoke();
        }

        void BtnHi_Click(object sender, RoutedEventArgs e) { brain.OnGreet(); }
        void BtnPet_Click(object sender, RoutedEventArgs e) { brain.OnPetButton(); }
        void BtnNap_Click(object sender, RoutedEventArgs e) { brain.OnNapButton(); }
        void BtnHop_Click(object sender, RoutedEventArgs e) { brain.OnHopButton(); }

        void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            if (MenuRequested != null) MenuRequested();
        }
    }
}
