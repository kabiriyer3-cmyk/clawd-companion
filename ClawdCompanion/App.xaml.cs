using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ClawdCompanion.Core;

namespace ClawdCompanion
{
    public partial class App : Application
    {
        Mutex mutex;
        bool ownsMutex;
        Settings settings;
        PetWindow pet;
        TrayIcon tray;
        MenuHost menuHost;

        protected override void OnStartup(StartupEventArgs e)
        {
            mutex = new Mutex(true, "ClawdCompanion.SingleInstance", out ownsMutex);
            if (!ownsMutex)
            {
                Shutdown();
                return;
            }
            base.OnStartup(e);

            DispatcherUnhandledException += (s, args) =>
            {
                Settings.Log("Unhandled: " + args.Exception);
                args.Handled = true;
            };

            settings = Settings.Load();
            try
            {
                pet = new PetWindow(settings);
            }
            catch (Exception ex)
            {
                Settings.Log("Startup: " + ex);
                MessageBox.Show("Clawd couldn't start:\n" + ex.Message, "Clawd Companion", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            menuHost = new MenuHost();
            pet.MenuRequested += ShowMenu;
            pet.HotkeyPressed += Toggle;

            tray = new TrayIcon();
            tray.LeftClick += Toggle;
            tray.RightClick += ShowMenu;

            if (settings.StartWithWindows) AutoStart.Apply(true); // keeps the path fresh if the exe moved
            pet.Wake();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            if (pet != null) pet.SaveState();
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (tray != null) { tray.Dispose(); tray = null; }
            if (mutex != null)
            {
                if (ownsMutex) { try { mutex.ReleaseMutex(); } catch { } }
                mutex.Dispose();
            }
            base.OnExit(e);
        }

        void Toggle()
        {
            if (pet == null) return;
            if (pet.IsAwake) pet.TuckAway(); else pet.Wake();
        }

        void Quit()
        {
            if (pet != null) { pet.SaveState(); pet.Close(); }
            Shutdown();
        }

        // ------------------------------------------------------------------ Codex-style menu

        void ShowMenu()
        {
            if (pet == null) return;
            menuHost.Show(BuildMenu());
        }

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu { Style = (Style)FindResource("CodexMenu") };
            bool awake = pet.IsAwake;

            m.Items.Add(Header(awake));
            m.Items.Add(Sep());
            if (awake)
            {
                m.Items.Add(Item("Say hi", null, () => pet.Brain.OnGreet()));
                m.Items.Add(Item("Pet Clawd", null, () => pet.Brain.OnPetButton()));
                m.Items.Add(Item(pet.Brain.IsSleeping ? "Wake up" : "Nap time", null, () => pet.Brain.OnNapButton()));
                m.Items.Add(Item("Hop", null, () => pet.Brain.OnHopButton()));
                m.Items.Add(Sep());
            }

            m.Items.Add(Section("Size"));
            foreach (string size in new[] { "Small", "Medium", "Large" })
            {
                string s = size;
                m.Items.Add(Item(s, null, () =>
                {
                    settings.Size = s;
                    settings.Save();
                    pet.ApplySettings();
                }, settings.Size == s));
            }
            m.Items.Add(Sep());

            m.Items.Add(Item("Walk around", null, () =>
            {
                settings.WalkAround = !settings.WalkAround;
                settings.Save();
                pet.ApplySettings();
            }, settings.WalkAround));
            m.Items.Add(Item("Sit on windows", null, () =>
            {
                settings.SitOnWindows = !settings.SitOnWindows;
                settings.Save();
                pet.ApplySettings();
            }, settings.SitOnWindows));
            m.Items.Add(Item("Start with Windows", null, () =>
            {
                settings.StartWithWindows = !settings.StartWithWindows;
                settings.Save();
                AutoStart.Apply(settings.StartWithWindows);
            }, settings.StartWithWindows));
            m.Items.Add(Sep());

            m.Items.Add(Item(awake ? "Tuck away pet" : "Wake pet", "Ctrl+Alt+C", Toggle));
            m.Items.Add(Item("Quit", null, Quit));
            return m;
        }

        MenuItem Header(bool awake)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(awake ? pet.Brain.StatusColor : Palette.Purple),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 9, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = "Clawd",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = (FontFamily)FindResource("Codex.UiFont"),
                Foreground = (Brush)FindResource("Codex.FgBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = awake ? pet.Brain.Status : "Tucked away",
                FontSize = 12,
                FontFamily = (FontFamily)FindResource("Codex.UiFont"),
                Foreground = (Brush)FindResource("Codex.MutedBrush"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            return new MenuItem { Header = row, Style = (Style)FindResource("CodexMenuHeader") };
        }

        MenuItem Section(string text)
        {
            return new MenuItem { Header = text, Style = (Style)FindResource("CodexMenuSection") };
        }

        Separator Sep()
        {
            return new Separator { Style = (Style)FindResource("CodexSeparator") };
        }

        MenuItem Item(string text, string gesture, Action action, bool isChecked = false)
        {
            var mi = new MenuItem
            {
                Header = text,
                Style = (Style)FindResource("CodexMenuItem"),
                IsChecked = isChecked
            };
            if (!string.IsNullOrEmpty(gesture)) mi.InputGestureText = gesture;
            mi.Click += (s, e) => action();
            return mi;
        }
    }
}
