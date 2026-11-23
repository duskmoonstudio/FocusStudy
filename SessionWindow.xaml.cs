using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FocusStudy
{
    public partial class SessionWindow : Window
    {
        sealed class BrowserTab
        {
            public WebView2 View;
            public Border Header;
            public TextBlock Label;
            public string LastFlagged;
            public CoreWebView2 Core => View.CoreWebView2;
        }

        sealed class FileEntry
        {
            public string Full;
            public bool IsDir;
            public string Display { get; set; }
        }

        sealed class PickItem
        {
            public string Full;
            public string Rel { get; set; }
        }

        const string ExitPledge = "I am choosing to end my focus session early.";

        static readonly HashSet<string> BlockedMenuItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "saveAs", "saveImageAs", "saveLinkAs", "saveMediaAs", "saveFrameAs", "print", "inspectElement",
            "webCapture", "share", "openLinkInNewWindow", "openLinkInNewInPrivateWindow", "createQrCode"
        };
        static readonly HashSet<string> ViewerExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp", ".mp4", ".webm", ".mp3", ".wav", ".ogg", ".m4a", ".html", ".htm"
        };
        static readonly HashSet<string> OfficeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xls", ".ods", ".csv", ".pptx", ".ppsx", ".potx"
        };
        static readonly HashSet<string> LegacyOfficeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".ppt", ".pps", ".odt", ".odp", ".rtf"
        };
        static readonly HashSet<string> TextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".xml", ".log", ".tex", ".c", ".cpp", ".h", ".java", ".js", ".ts", ".css", ".ipynb", ".yaml", ".yml", ".ini"
        };
        static readonly Dictionary<string, string> Mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif", [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".txt"] = "text/plain",
            [".md"] = "text/markdown", [".csv"] = "text/csv", [".json"] = "application/json", [".py"] = "text/x-python",
            [".m"] = "text/plain", [".html"] = "text/html", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4", [".zip"] = "application/zip"
        };

        readonly Settings _s;
        TimeSpan _planned;       // length of the current block (changes when the user continues)
        readonly bool _demo;
        readonly string _snapDir;
        readonly string _root;
        FocusMonitor _mon;       // stats for the current block
        int _block = 1;          // 1 = first session, 2+ = extra time added from the summary
        readonly Stopwatch _sw = new Stopwatch();
        readonly CodeRunner _runner = new CodeRunner();
        readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        readonly List<BlackoutWindow> _blackouts = new List<BlackoutWindow>();
        readonly HashSet<string> _whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<uint, string> _procNames = new Dictionary<uint, string>();

        KeyboardLock _kb;
        DispatcherTimer _tick, _guard, _bannerTimer, _cooldown;
        CoreWebView2Environment _env;
        WebView2 _viewer;
        string _viewerText;
        Task _viewerInit;
        BrowserTab _cur, _flagTab;
        CoreWebView2 _pickerCore;
        FrameworkElement _overlay;
        Action<string> _promptOk;
        Func<string, string> _promptValidate;
        IntPtr _hwnd;
        bool _ended, _allowClose, _resumeDistract;
        TimeSpan _idleStart, _cooldownStart;
        int _exitStep, _mathAnswer;
        string _dir, _codeFile, _lang = "python";
        bool _dirty, _loadingCode;
        string _mEngine;

        public SessionWindow(Settings s, TimeSpan planned, bool demo, string snapDir)
        {
            InitializeComponent();
            _s = s;
            _planned = planned;
            _demo = demo;
            _snapDir = snapDir;
            _root = Path.GetFullPath(s.StudyFolder).TrimEnd('\\');
            _mon = new FocusMonitor(s);

            // No title bar in any mode: no minimize, maximize or close buttons. The only ways out are
            // the session ending (summary → Close) or completing the exit steps (Alt+F4).
            Left = 0;
            Top = 0;
            WindowState = WindowState.Maximized;
            if (demo)
            {
                Title = "Focus Study (demo, no lock)";
                ShowInTaskbar = true; // demo: keyboard isn't locked and the window isn't forced on top
            }
            else
            {
                Topmost = true;
            }

            DataObject.AddPastingHandler(ExitPhraseBox, (_, e) => e.CancelCommand());
            // The pledge must be typed: no dragging text in either.
            ExitPhraseBox.AllowDrop = false;
            DragEventHandler noDrop = (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
            ExitPhraseBox.PreviewDragEnter += noDrop;
            ExitPhraseBox.PreviewDragOver += noDrop;
            ExitPhraseBox.PreviewDrop += noDrop;
            SourceInitialized += (_, __) => _hwnd = new WindowInteropHelper(this).Handle;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        // =====================================================================
        // Session lifecycle
        // =====================================================================

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_demo)
            {
                _kb = new KeyboardLock();
                _kb.AltF4 += RequestExit;
                _kb.Install();
                _kb.Active = true;
                CreateBlackouts();
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
                _guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _guard.Tick += Guard_Tick;
                _guard.Start();
            }

            _sw.Start();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += Tick;
            _tick.Start();

            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _bannerTimer.Tick += (_, __) => { _bannerTimer.Stop(); TopBanner.Visibility = Visibility.Collapsed; };

            ThemeBtn.Content = Theme.ToggleLabel;
            Theme.Changed += OnThemeChanged;
            InitCode();
            InitNotes();
            LoadFolder(_root);
            ShowPage("browser");

            if (_snapDir != null) { _ = RunSnapshots(); return; }
            if (_demo) ShowBanner("Demo mode: same screen as a real session, but nothing is locked.");

            try
            {
                _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.LocalDir, "WebView2"));
                await AddTab(_s.HomePage);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowBanner("The browser couldn't start: " + ex.Message);
            }
        }

        void CreateBlackouts()
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (screen.Primary) continue;
                var w = new BlackoutWindow(screen.Bounds, this);
                w.Show();
                _blackouts.Add(w);
            }
        }

        /// <summary>A monitor plugged in mid-session gets blacked out too.</summary>
        void OnDisplaysChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_allowClose) return;
            foreach (var b in _blackouts) { b.AllowClose = true; b.Close(); }
            _blackouts.Clear();
            CreateBlackouts();
            if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; WindowState = WindowState.Maximized; }
        }));

        /// <summary>Windows is signing out or shutting down: keep the user's work and record the session.</summary>
        public void SaveForShutdown()
        {
            try
            {
                AutoSaveCode();
                SaveNote();
                if (!_ended)
                {
                    _ended = true;
                    _sw.Stop();
                    if (_snapDir == null) _mon.AppendLog(_planned, _sw.Elapsed, false);
                }
                _runner.Shutdown();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void Tick(object sender, EventArgs e)
        {
            if (_ended) return;
            if (_sw.Elapsed >= _planned) { EndSession(true); return; }

            uint idleMs = NativeMethods.IdleMilliseconds();
            if (_overlay == null && idleMs > (uint)_s.IdleMinutes * 60_000u && !IsMediaPlaying() && !_runner.IsRunning)
            {
                _idleStart = _sw.Elapsed - TimeSpan.FromMilliseconds(idleMs);
                if (_idleStart < TimeSpan.Zero) _idleStart = TimeSpan.Zero;
                _mon.IdleEvents++;
                _mon.Mark(_sw.Elapsed);
                ShowOverlay(IdleCard);
            }
            UpdateFocusIndicator();
        }

        /// <summary>Keeps the session window in front. Task Manager (reached via Ctrl+Alt+Del) is let through.</summary>
        void Guard_Tick(object sender, EventArgs e)
        {
            if (_ended || _hwnd == IntPtr.Zero) return;
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == _hwnd)
            {
                if (!Topmost) Topmost = true;
                return;
            }
            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            string name = pid == (uint)Environment.ProcessId ? "self" : ProcName(pid);

            // Native Windows file dialogs (e.g. an upload button inside an embedded frame, PDF "Save as",
            // print-to-PDF) could browse the whole PC. Close them; the in-app picker covers uploads.
            if ((name == "self" || name == "msedgewebview2") && NativeMethods.ClassName(fg) == "#32770")
            {
                NativeMethods.PostMessage(fg, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                ShowBanner("Windows file dialogs are disabled during a session. Files can only come from your study folder.");
                return;
            }
            if (name == "self") return;
            if (name == "taskmgr")
            {
                if (Topmost) Topmost = false; // let the emergency path be usable
                return;
            }
            if (name == "msedgewebview2" || name == "textinputhost" || name == "ctfmon") return; // browser popups, IME

            if (!Topmost) Topmost = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            NativeMethods.ForceForeground(_hwnd);
        }

        string ProcName(uint pid)
        {
            if (pid == 0) return "";
            if (_procNames.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById((int)pid); n = p.ProcessName.ToLowerInvariant(); }
            catch { n = ""; }
            if (_procNames.Count > 500) _procNames.Clear();
            _procNames[pid] = n;
            return n;
        }

        bool IsMediaPlaying()
        {
            try
            {
                if (_tabs.Any(t => t.Core != null && t.Core.IsDocumentPlayingAudio)) return true;
                if (_viewer?.CoreWebView2 != null && _viewer.CoreWebView2.IsDocumentPlayingAudio) return true;
            }
            catch { }
            return false;
        }

        void UpdateFocusIndicator()
        {
            int recent = _mon.RecentEvents(_sw.Elapsed, TimeSpan.FromMinutes(15));
            if (recent >= 3)
            {
                FocusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Danger");
                FocusText.Text = "Lots of drifting. Pick one task and go.";
            }
            else if (recent >= 1)
            {
                FocusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Warn");
                FocusText.Text = "Refocus. You've got this.";
            }
            else
            {
                FocusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Good");
                FocusText.Text = "Focused";
            }
        }

        void EndSession(bool completed)
        {
            if (_ended) return;
            _ended = true;
            _sw.Stop();
            _tick?.Stop();
            _cooldown?.Stop();
            _runner.Stop();
            if (_overlay == IdleCard) _mon.IdleTime += _sw.Elapsed - _idleStart;
            AutoSaveCode();
            SaveNote();
            if (_snapDir == null) _mon.AppendLog(_planned, _sw.Elapsed, completed);

            SummaryTitle.Text = completed
                ? (_block == 1 ? "Session complete. Well done." : "Extra time complete. Great effort.")
                : "Session ended early";
            SummaryText.Text = _mon.Summary(_planned, _sw.Elapsed, completed);
            ShowOverlay(SummaryCard);
        }

        void Unlock()
        {
            _allowClose = true;
            _kb?.Dispose();
            _guard?.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            Theme.Changed -= OnThemeChanged;
            _runner.Shutdown();
            foreach (var b in _blackouts) { b.AllowClose = true; b.Close(); }
            Application.Current.Shutdown();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (_allowClose || App.ForceClose) return;
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(RequestExit));
        }

        void SummaryUnlock_Click(object sender, RoutedEventArgs e) => Unlock();

        /// <summary>
        /// "Keep going": starts another block straight from the summary, with every tab, file, code and note
        /// as it was. The lock never lifts in between. Each block gets its own summary and its own log row.
        /// </summary>
        void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (!_ended) return;
            int minutes = int.Parse((string)((Button)sender).Tag);
            _planned = TimeSpan.FromMinutes(minutes);
            _mon = new FocusMonitor(_s);
            _block++;
            _ended = false;
            _sw.Restart();
            _tick?.Start();
            HideOverlay();
            UpdateFocusIndicator();
            ShowBanner(minutes >= 60 ? "Keep going: one more hour added." : $"Keep going: {minutes} more minutes added.");
        }

        // =====================================================================
        // Overlays & banner
        // =====================================================================

        /// <summary>Exit steps open beside the study content (still visible and usable), not over it.</summary>
        void ShowExitPanel()
        {
            OverlayRoot.Visibility = Visibility.Collapsed;
            ContentRoot.Visibility = Visibility.Visible;
            ExitCard.Visibility = Visibility.Visible;
            _overlay = ExitCard;
        }

        void ShowOverlay(FrameworkElement card)
        {
            ExitCard.Visibility = Visibility.Collapsed;
            foreach (UIElement c in OverlayRoot.Children) c.Visibility = c == card ? Visibility.Visible : Visibility.Collapsed;
            OverlayRoot.Visibility = Visibility.Visible;
            ContentRoot.Visibility = Visibility.Hidden; // native browser surfaces would otherwise draw on top
            _overlay = card;
        }

        void HideOverlay()
        {
            ExitCard.Visibility = Visibility.Collapsed;
            OverlayRoot.Visibility = Visibility.Collapsed;
            ContentRoot.Visibility = Visibility.Visible;
            _overlay = null;
        }

        void ShowBanner(string text)
        {
            TopBannerText.Text = text;
            TopBanner.Visibility = Visibility.Visible;
            _bannerTimer?.Stop();
            _bannerTimer?.Start();
        }

        void FocusLater(UIElement el) =>
            Dispatcher.BeginInvoke(new Action(() => { el.Focus(); Keyboard.Focus(el as IInputElement); }), DispatcherPriority.Input);

        void IdleBack_Click(object sender, RoutedEventArgs e)
        {
            _mon.IdleTime += _sw.Elapsed - _idleStart;
            HideOverlay();
        }

        void Prompt(string title, string initial, Func<string, string> validate, Action<string> ok)
        {
            PromptTitle.Text = title;
            PromptBox.Text = initial ?? "";
            PromptError.Text = "";
            _promptValidate = validate;
            _promptOk = ok;
            ShowOverlay(PromptCard);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PromptBox.Focus();
                int dot = PromptBox.Text.LastIndexOf('.');
                PromptBox.Select(0, dot > 0 ? dot : PromptBox.Text.Length);
            }), DispatcherPriority.Input);
        }

        void PromptOk_Click(object sender, RoutedEventArgs e)
        {
            string v = PromptBox.Text.Trim();
            string err = _promptValidate?.Invoke(v);
            if (err != null) { PromptError.Text = err; return; }
            HideOverlay();
            _promptOk?.Invoke(v);
        }

        void PromptCancel_Click(object sender, RoutedEventArgs e) => HideOverlay();

        void PromptCard_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { PromptOk_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideOverlay(); e.Handled = true; }
        }

        // =====================================================================
        // Exit steps (Alt+F4)
        // =====================================================================

        void RequestExit()
        {
            if (_ended) { Unlock(); return; }
            if (_overlay == ExitCard) return;
            if (_overlay == IdleCard) _mon.IdleTime += _sw.Elapsed - _idleStart;
            if (_overlay == PickerCard) SendFiles(new List<object>());
            _resumeDistract = _overlay == DistractCard; // "Stay" must not quietly dismiss an off-topic warning

            _mon.ExitAttempts++;
            _mon.Mark(_sw.Elapsed);
            _exitStep = 1;
            ExitReason.Text = "";
            ExitPhraseBox.Text = "";
            ExitMathBox.Text = "";
            var r = new Random();
            int a = r.Next(13, 50), b = r.Next(13, 50);
            _mathAnswer = a * b;
            ExitMathQ.Text = $"{a} × {b} = ?";
            ExitPhrase.Text = "“" + ExitPledge + "”";
            ShowExitPanel();
            ShowExitStep();
        }

        void ShowExitStep()
        {
            ExitStep1.Visibility = _exitStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            ExitStep2.Visibility = _exitStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            ExitStep3.Visibility = _exitStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            ExitStep4.Visibility = _exitStep == 4 ? Visibility.Visible : Visibility.Collapsed;
            ExitStepLabel.Text = $"STEP {_exitStep} OF 4";
            ExitError.Text = "";
            ExitNext.Visibility = _exitStep < 4 ? Visibility.Visible : Visibility.Collapsed;
            ExitUnlock.Visibility = _exitStep == 4 ? Visibility.Visible : Visibility.Collapsed;

            if (_exitStep == 1) FocusLater(ExitReason);
            else if (_exitStep == 2) FocusLater(ExitPhraseBox);
            else if (_exitStep == 3) FocusLater(ExitMathBox);
            else StartCooldown();
        }

        void ExitNext_Click(object sender, RoutedEventArgs e)
        {
            switch (_exitStep)
            {
                case 1:
                    if (ExitReason.Text.Trim().Length < 20) { ExitError.Text = "Write at least 20 characters about why you're stopping."; return; }
                    break;
                case 2:
                    if (!string.Equals(ExitPhraseBox.Text.Trim(), ExitPledge, StringComparison.Ordinal))
                    { ExitError.Text = "The sentence has to match exactly, including capitals and the full stop."; return; }
                    break;
                case 3:
                    if (!int.TryParse(ExitMathBox.Text.Trim(), out int v) || v != _mathAnswer)
                    { ExitError.Text = "Not quite. Try again."; ExitMathBox.SelectAll(); return; }
                    break;
            }
            _exitStep++;
            ShowExitStep();
        }

        void ExitCard_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _exitStep < 4) { ExitNext_Click(null, null); e.Handled = true; }
        }

        void StartCooldown()
        {
            ExitUnlock.IsEnabled = false;
            ExitProgress.Minimum = 0;
            ExitProgress.Maximum = Math.Max(5, _s.ExitCooldownSeconds);
            ExitProgress.Value = 0;
            ExitCooldownText.Text = "Waiting…";
            _cooldownStart = _sw.Elapsed;
            _cooldown?.Stop();
            _cooldown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _cooldown.Tick += (_, __) =>
            {
                double secs = (_sw.Elapsed - _cooldownStart).TotalSeconds;
                ExitProgress.Value = Math.Min(secs, ExitProgress.Maximum);
                if (secs >= ExitProgress.Maximum)
                {
                    _cooldown.Stop();
                    ExitUnlock.IsEnabled = true;
                    ExitCooldownText.Text = "You can unlock now, or go back and finish what you started.";
                }
            };
            _cooldown.Start();
        }

        void ExitStay_Click(object sender, RoutedEventArgs e)
        {
            _cooldown?.Stop();
            _mon.ExitAbandoned++;
            HideOverlay();
            if (_resumeDistract) { _resumeDistract = false; ShowOverlay(DistractCard); }
        }

        void ExitUnlock_Click(object sender, RoutedEventArgs e)
        {
            _mon.ExitReason = ExitReason.Text;
            EndSession(false);
        }

        // =====================================================================
        // Navigation between pages & keyboard shortcuts
        // =====================================================================

        void Nav_Click(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);

        // =====================================================================
        // Light / dark mode
        // =====================================================================

        void Theme_Click(object sender, RoutedEventArgs e)
        {
            _s.Theme = Theme.Toggle();
            try { _s.Save(); } catch { }
        }

        void OnThemeChanged()
        {
            ThemeBtn.Content = Theme.ToggleLabel;
            foreach (var t in _tabs) if (t.Core != null) ApplyColorScheme(t.Core);
            if (_viewer?.CoreWebView2 != null)
            {
                ApplyColorScheme(_viewer.CoreWebView2);
                if (_viewerText != null) _ = OpenInViewer(_viewerText, true); // re-render plain-text preview in the new colours
            }
        }

        /// <summary>Websites that support it (and the document viewers) follow the app's light / dark mode.</summary>
        static void ApplyColorScheme(CoreWebView2 c)
        {
            try
            {
                c.Profile.PreferredColorScheme = Theme.IsLight
                    ? CoreWebView2PreferredColorScheme.Light
                    : CoreWebView2PreferredColorScheme.Dark;
            }
            catch { }
        }

        void ShowPage(string page)
        {
            BrowserPage.Visibility = page == "browser" ? Visibility.Visible : Visibility.Collapsed;
            FilesPage.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
            CodePage.Visibility = page == "code" ? Visibility.Visible : Visibility.Collapsed;
            StyleToggle(NavBrowser, page == "browser");
            StyleToggle(NavFiles, page == "files");
            StyleToggle(NavCode, page == "code");
            if (page == "files") LoadFolder(_dir ?? _root);
        }

        void StyleToggle(Button b, bool on)
        {
            b.SetResourceReference(ForegroundProperty, on ? "Text" : "Muted");
            if (on) b.SetResourceReference(BackgroundProperty, "Panel2"); else b.Background = Brushes.Transparent;
        }

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_overlay != null) return;
            if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                NotesToggle_Click(null, null);
                e.Handled = true;
                return;
            }
            bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (ctrl && e.Key == Key.S && NotesBox.IsKeyboardFocusWithin)
            {
                SaveNote();
                e.Handled = true;
                return;
            }
            if (CodePage.Visibility == Visibility.Visible)
            {
                if (ctrl && e.Key == Key.S) { CodeSave_Click(null, null); e.Handled = true; }
                else if (e.Key == Key.F5) { Run_Click(null, null); e.Handled = true; }
            }
            else if (BrowserPage.Visibility == Visibility.Visible)
            {
                if (ctrl && e.Key == Key.T) { NewTab_Click(null, null); e.Handled = true; }
                else if (ctrl && e.Key == Key.W && _cur != null) { CloseTab(_cur); e.Handled = true; }
                else if (ctrl && e.Key == Key.L) { AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true; }
            }
        }

        // =====================================================================
        // Browser
        // =====================================================================

        void ApplySafeSettings(CoreWebView2 c)
        {
            ApplyColorScheme(c);
            var st = c.Settings;
            st.AreDevToolsEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false; // no Save As, Print dialog, view-source windows, etc.
            st.IsStatusBarEnabled = false;
            st.IsZoomControlEnabled = true;
            // PDF toolbar: "Save as" opens a Windows dialog for the whole PC; downloads go via the Downloads folder.
            st.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.SaveAs;
            c.ContextMenuRequested += FilterContextMenu;
            c.PermissionRequested += (_, e) =>
            {
                // Website pop-up notifications are a distraction: always refuse.
                if (e.PermissionKind == CoreWebView2PermissionKind.Notifications) e.State = CoreWebView2PermissionState.Deny;
            };
            c.LaunchingExternalUriScheme += (_, e) =>
            {
                e.Cancel = true;
                ShowBanner("Opening other apps is disabled during a focus session.");
            };
        }

        static void FilterContextMenu(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            var items = e.MenuItems;
            for (int i = items.Count - 1; i >= 0; i--)
                if (BlockedMenuItems.Contains(items[i].Name)) items.RemoveAt(i);
        }

        async Task<BrowserTab> AddTab(string url)
        {
            if (_env == null) return null;
            if (_tabs.Count >= 10) { ShowBanner("Tab limit reached. Close a tab first."); return null; }

            var wv = new WebView2();
            var tab = new BrowserTab { View = wv };
            BrowserHost.Children.Add(wv);

            tab.Label = new TextBlock
            {
                Text = "New tab", MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5
            };
            var close = new Button { Content = "×", Style = (Style)FindResource("TabClose") };
            close.Click += (_, __) => CloseTab(tab);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(tab.Label);
            sp.Children.Add(close);
            tab.Header = new Border
            {
                Child = sp, Height = 32, Padding = new Thickness(12, 0, 4, 0), Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(8, 8, 0, 0), Cursor = Cursors.Hand
            };
            tab.Header.MouseLeftButtonUp += (_, __) => ActivateTab(tab);
            TabStrip.Children.Add(tab.Header);
            _tabs.Add(tab);
            ActivateTab(tab);

            await wv.EnsureCoreWebView2Async(_env);
            await ConfigureBrowserCore(tab);
            if (url != null) wv.CoreWebView2.Navigate(url);
            return tab;
        }

        async Task ConfigureBrowserCore(BrowserTab tab)
        {
            var c = tab.Core;
            ApplySafeSettings(c);
            await c.AddScriptToExecuteOnDocumentCreatedAsync(WebScripts.FilePicker);

            c.NavigationStarting += (_, e) =>
            {
                if (!AllowUrl(e.Uri, out string reason)) { e.Cancel = true; OnBlocked(reason); }
            };
            c.FrameNavigationStarting += (_, e) =>
            {
                if (!AllowUrl(e.Uri, out string ignored)) e.Cancel = true;
            };
            c.SourceChanged += (_, __) => { if (tab == _cur && !AddressBox.IsKeyboardFocused) AddressBox.Text = c.Source; };
            c.DocumentTitleChanged += (_, __) =>
            {
                tab.Label.Text = string.IsNullOrWhiteSpace(c.DocumentTitle) ? "New tab" : c.DocumentTitle;
                tab.Header.ToolTip = c.DocumentTitle;
                CheckPage(tab);
            };
            c.NavigationCompleted += (_, __) => { UpdateNavButtons(); CheckPage(tab); };
            c.HistoryChanged += (_, __) => UpdateNavButtons();
            c.NewWindowRequested += OnNewWindowRequested;
            c.WindowCloseRequested += (_, __) => CloseTab(tab);
            c.DownloadStarting += OnDownloadStarting;
            c.WebMessageReceived += (_, e) => OnWebMessage(c, e);
        }

        void ActivateTab(BrowserTab tab)
        {
            _cur = tab;
            foreach (var t in _tabs)
            {
                bool on = t == tab;
                t.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) t.Header.SetResourceReference(Border.BackgroundProperty, "Panel2"); else t.Header.Background = Brushes.Transparent;
                t.Label.SetResourceReference(TextBlock.ForegroundProperty, on ? "Text" : "Muted");
            }
            AddressBox.Text = tab.Core?.Source ?? "";
            UpdateNavButtons();
        }

        void CloseTab(BrowserTab tab)
        {
            if (!_tabs.Contains(tab)) return;
            int idx = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            TabStrip.Children.Remove(tab.Header);
            BrowserHost.Children.Remove(tab.View);
            try { tab.View.Dispose(); } catch { }
            if (_tabs.Count == 0) { _cur = null; _ = AddTab(_s.HomePage); return; }
            if (_cur == tab) ActivateTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }

        void UpdateNavButtons()
        {
            BackBtn.IsEnabled = _cur?.Core?.CanGoBack == true;
            FwdBtn.IsEnabled = _cur?.Core?.CanGoForward == true;
        }

        bool AllowUrl(string uri, out string reason)
        {
            reason = null;
            if (!Uri.TryCreate(uri ?? "", UriKind.Absolute, out var u)) return true;
            switch (u.Scheme)
            {
                case "http":
                case "https":
                    break;
                case "about":
                case "data":
                case "blob":
                    return true;
                default:
                    reason = $"“{u.Scheme}:” links are disabled during a focus session.";
                    return false;
            }
            var bad = _mon.BlockedMatch(u.Host);
            if (bad != null)
            {
                reason = $"{bad} is blocked during focus sessions.";
                return false;
            }
            return true;
        }

        void OnBlocked(string reason)
        {
            _mon.BlockedAttempts++;
            _mon.Mark(_sw.Elapsed);
            ShowBanner(reason);
            UpdateFocusIndicator();
        }

        static string PageKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int hash = url.IndexOf('#');
            return hash >= 0 ? url.Substring(0, hash) : url;
        }

        void CheckPage(BrowserTab tab)
        {
            if (_ended || tab.Core == null || _overlay != null) return;
            string url = tab.Core.Source, title = tab.Core.DocumentTitle ?? "";
            string key = PageKey(url);
            if (_whitelist.Contains(key)) return;
            var v = _mon.Classify(url, title);
            if (!v.Distracting) return;
            if (tab.LastFlagged == key + "|" + title) return;
            tab.LastFlagged = key + "|" + title;

            _mon.Warnings++;
            _mon.Mark(_sw.Elapsed);
            _flagTab = tab;
            DistractTitle.Text = string.IsNullOrWhiteSpace(title) ? new Uri(url).Host : title;
            DistractReason.Text = v.Reason;
            ActivateTab(tab);
            ShowPage("browser");
            ShowOverlay(DistractCard);
            UpdateFocusIndicator();
        }

        void DistractBack_Click(object sender, RoutedEventArgs e)
        {
            HideOverlay();
            var t = _flagTab;
            if (t?.Core == null) return;
            if (t.Core.CanGoBack) t.Core.GoBack();
            else if (_tabs.Count > 1) CloseTab(t);
            else t.Core.Navigate(_s.HomePage);
        }

        void DistractAllow_Click(object sender, RoutedEventArgs e)
        {
            if (_flagTab?.Core != null) _whitelist.Add(PageKey(_flagTab.Core.Source));
            _mon.Overrides++;
            HideOverlay();
        }

        async void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (!AllowUrl(e.Uri, out string reason))
            {
                e.Handled = true;
                OnBlocked(reason);
                return;
            }
            var deferral = e.GetDeferral();
            try
            {
                ShowPage("browser");
                var tab = await AddTab(null);
                if (tab != null) e.NewWindow = tab.Core;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                e.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        }

        void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            string dir = Path.Combine(_root, "Downloads");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(name)) name = "download";
            e.ResultFilePath = UniquePath(Path.Combine(dir, name));
            e.Handled = true;
            var op = e.DownloadOperation;
            op.StateChanged += (_, __) =>
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    ShowBanner($"Saved to Study Files › Downloads › {Path.GetFileName(op.ResultFilePath)}");
                    if (FilesPage.Visibility == Visibility.Visible) LoadFolder(_dir);
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    ShowBanner("Download failed.");
                }
            };
            ShowBanner("Downloading to Study Files › Downloads…");
        }

        void NewTab_Click(object sender, RoutedEventArgs e) => _ = AddTab(_s.HomePage);
        void Back_Click(object sender, RoutedEventArgs e) { if (_cur?.Core?.CanGoBack == true) _cur.Core.GoBack(); }
        void Fwd_Click(object sender, RoutedEventArgs e) { if (_cur?.Core?.CanGoForward == true) _cur.Core.GoForward(); }
        void Reload_Click(object sender, RoutedEventArgs e) => _cur?.Core?.Reload();
        void Home_Click(object sender, RoutedEventArgs e) => NavigateCurrent(_s.HomePage);
        void Quick_Click(object sender, RoutedEventArgs e) => NavigateCurrent((string)((Button)sender).Tag);

        void NavigateCurrent(string url)
        {
            if (_cur?.Core == null) { _ = AddTab(url); return; }
            _cur.Core.Navigate(url);
        }

        void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(AddressBox.SelectAll), DispatcherPriority.Input);

        void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            string t = AddressBox.Text.Trim();
            if (t.Length == 0) return;
            string url;
            if (t.Contains(" ") || (!t.Contains(".") && !t.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                url = "https://www.google.com/search?q=" + Uri.EscapeDataString(t);
            else if (!t.Contains("://"))
                url = "https://" + t;
            else
                url = t;
            NavigateCurrent(url);
            _cur?.View.Focus();
            e.Handled = true;
        }

        // ---------- upload picker (study folder only) ----------

        void OnWebMessage(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try { msg = e.TryGetWebMessageAsString(); } catch { return; }
            try
            {
                using var doc = JsonDocument.Parse(msg);
                if (!doc.RootElement.TryGetProperty("type", out var type) || type.GetString() != "pickFiles") return;
                bool multiple = doc.RootElement.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True;
                _pickerCore = core;
                if (_overlay != null) { SendFiles(new List<object>()); return; }
                var files = EnumerateStudyFiles()
                    .Select(p => new PickItem { Full = p, Rel = Path.GetRelativePath(_root, p) })
                    .ToList();
                PickerList.SelectionMode = multiple ? SelectionMode.Multiple : SelectionMode.Single;
                PickerList.ItemsSource = files;
                PickerEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                PickerError.Text = "";
                ShowOverlay(PickerCard);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        IEnumerable<string> EnumerateStudyFiles()
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            try
            {
                return Directory.EnumerateFiles(_root, "*", opts)
                    .Where(p => !Path.GetRelativePath(_root, p).StartsWith("."))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Take(3000)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        void PickerList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PickerList.SelectionMode == SelectionMode.Single) PickerAttach_Click(null, null);
        }

        void PickerAttach_Click(object sender, RoutedEventArgs e)
        {
            var sel = PickerList.SelectedItems.Cast<PickItem>().ToList();
            if (sel.Count == 0) { PickerError.Text = "Select at least one file."; return; }
            long total = sel.Sum(p => { try { return new FileInfo(p.Full).Length; } catch { return 0L; } });
            if (total > 40L * 1024 * 1024) { PickerError.Text = "That's more than 40 MB. Choose fewer or smaller files."; return; }

            var payload = new List<object>();
            foreach (var p in sel)
            {
                try
                {
                    string ext = Path.GetExtension(p.Full);
                    payload.Add(new
                    {
                        name = Path.GetFileName(p.Full),
                        mime = Mime.TryGetValue(ext, out var mt) ? mt : "application/octet-stream",
                        data = Convert.ToBase64String(File.ReadAllBytes(p.Full))
                    });
                }
                catch (Exception ex)
                {
                    PickerError.Text = $"Couldn't read {p.Rel}: {ex.Message}";
                    return;
                }
            }
            HideOverlay();
            SendFiles(payload);
        }

        void PickerCancel_Click(object sender, RoutedEventArgs e)
        {
            HideOverlay();
            SendFiles(new List<object>());
        }

        void SendFiles(List<object> files)
        {
            var core = _pickerCore;
            _pickerCore = null;
            if (core == null) return;
            try { core.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "files", files })); }
            catch { }
        }

        // =====================================================================
        // Study files
        // =====================================================================

        bool IsInsideRoot(string path)
        {
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                return full.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        void LoadFolder(string dir)
        {
            if (dir == null || !IsInsideRoot(dir) || !Directory.Exists(dir)) dir = _root;
            _dir = Path.GetFullPath(dir).TrimEnd('\\');
            var list = new List<FileEntry>();
            try
            {
                foreach (var d in Directory.GetDirectories(_dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith(".")) continue;
                    list.Add(new FileEntry { Full = d, IsDir = true, Display = "📁   " + name });
                }
                foreach (var f in Directory.GetFiles(_dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith(".")) continue;
                    list.Add(new FileEntry { Full = f, Display = FileIcon(Path.GetExtension(f)) + "   " + name });
                }
            }
            catch (Exception ex)
            {
                ShowBanner("Couldn't read folder: " + ex.Message);
            }
            FileList.ItemsSource = list;
            string rel = _dir.Equals(_root, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetRelativePath(_root, _dir);
            FolderPath.Text = rel.Length == 0 ? "Study Folder" : "Study Folder › " + rel.Replace("\\", " › ");
            UpBtn.IsEnabled = rel.Length > 0;
        }

        static string FileIcon(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".pdf": return "📕";
                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".svg": case ".webp": return "🖼";
                case ".mp4": case ".webm": case ".mp3": case ".wav": case ".ogg": case ".m4a": return "🎞";
                case ".py": return "🐍";
                case ".m": return "📐";
                case ".txt": case ".md": return "📝";
                default: return "📄";
            }
        }

        void Up_Click(object sender, RoutedEventArgs e)
        {
            var parent = Path.GetDirectoryName(_dir);
            LoadFolder(parent != null && IsInsideRoot(parent) ? parent : _root);
        }

        void Refresh_Click(object sender, RoutedEventArgs e) => LoadFolder(_dir);

        void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is FileEntry fe) OpenEntry(fe);
        }

        void FileList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && FileList.SelectedItem is FileEntry fe) { OpenEntry(fe); e.Handled = true; }
            else if (e.Key == Key.Back) { Up_Click(null, null); e.Handled = true; }
        }

        static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
            if (name.Length > 120) return "That name is too long.";
            if (name == "." || name == ".." || name.StartsWith(".")) return "Names can't start with a dot.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Names can't contain \\ / : * ? \" < > |";
            return null;
        }

        void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            Prompt("New folder", "New folder", n =>
            {
                var err = ValidateName(n);
                if (err != null) return err;
                return Directory.Exists(Path.Combine(_dir, n)) ? "A folder with that name already exists." : null;
            }, n =>
            {
                try { Directory.CreateDirectory(Path.Combine(_dir, n)); } catch (Exception ex) { ShowBanner(ex.Message); }
                LoadFolder(_dir);
            });
        }

        void NewNote_Click(object sender, RoutedEventArgs e)
        {
            Prompt("New note", "notes.md", n =>
            {
                var err = ValidateName(n);
                if (err != null) return err;
                if (!Path.HasExtension(n)) n += ".md";
                return File.Exists(Path.Combine(_dir, n)) ? "A file with that name already exists." : null;
            }, n =>
            {
                if (!Path.HasExtension(n)) n += ".md";
                var path = Path.Combine(_dir, n);
                try
                {
                    File.WriteAllText(path, "# " + Path.GetFileNameWithoutExtension(n) + Environment.NewLine + Environment.NewLine);
                    LoadFolder(_dir);
                    OpenNote(path);
                }
                catch (Exception ex) { ShowBanner(ex.Message); }
            });
        }

        void OpenEntry(FileEntry fe)
        {
            if (!IsInsideRoot(fe.Full)) return;
            if (fe.IsDir) { LoadFolder(fe.Full); return; }
            string ext = Path.GetExtension(fe.Full);
            if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m", StringComparison.OrdinalIgnoreCase))
            {
                OpenInCode(fe.Full);
                return;
            }
            if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                OpenNote(fe.Full);
                return;
            }
            if (OfficeExts.Contains(ext))
            {
                _ = OpenInViewer(fe.Full, false, ext.TrimStart('.').ToLowerInvariant());
                return;
            }
            if (ViewerExts.Contains(ext) || TextExts.Contains(ext))
            {
                _ = OpenInViewer(fe.Full, TextExts.Contains(ext));
                return;
            }
            if (LegacyOfficeExts.Contains(ext))
            {
                ShowViewerMessage(
                    $"{Path.GetFileName(fe.Full)} is an older Office / OpenDocument format that Focus Study can't display.\n\n" +
                    "Before your next session, save it as .docx / .pptx (or PDF) and it will open here.");
                return;
            }
            ShowViewerMessage($"{Path.GetFileName(fe.Full)} can't be opened inside Focus Study.");
        }

        void ShowViewerMessage(string text)
        {
            ViewerMessage.Text = text;
            ViewerMessage.Visibility = Visibility.Visible;
            if (_viewer != null) _viewer.Visibility = Visibility.Collapsed;
        }

        async Task EnsureViewer()
        {
            if (_viewer == null)
            {
                _viewer = new WebView2();
                ViewerHost.Children.Add(_viewer);
                _viewerInit = InitViewerCore();
            }
            _viewer.Visibility = Visibility.Visible;
            ViewerMessage.Visibility = Visibility.Collapsed;
            await _viewerInit;
        }

        async Task InitViewerCore()
        {
            await _viewer.EnsureCoreWebView2Async(_env);
            var c = _viewer.CoreWebView2;
            ApplySafeSettings(c);
            c.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs;
            await c.AddScriptToExecuteOnDocumentCreatedAsync(WebScripts.FilePicker); // HTML files in the study folder
            c.WebMessageReceived += (_, e) => OnWebMessage(c, e);
            c.SetVirtualHostNameToFolderMapping("study.local", _root, CoreWebView2HostResourceAccessKind.Allow);
            // Bundled open-source document viewers (docx-preview, SheetJS, own pptx renderer)
            c.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "viewers"), CoreWebView2HostResourceAccessKind.Allow);
            c.NavigationStarting += (_, e) =>
            {
                var u = e.Uri ?? "";
                if (u.StartsWith("https://study.local/", StringComparison.OrdinalIgnoreCase) ||
                    u.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase) ||
                    u.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                    u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { ShowPage("browser"); NavigateOrNewTab(u); }
            };
            c.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if ((e.Uri ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)) { ShowPage("browser"); NavigateOrNewTab(e.Uri); }
            };
            c.DownloadStarting += (_, e) => e.Cancel = true;
        }

        void NavigateOrNewTab(string url)
        {
            if (!AllowUrl(url, out var reason)) { OnBlocked(reason); return; }
            _ = AddTab(url);
        }

        async Task OpenInViewer(string path, bool asText, string officeType = null)
        {
            if (_env == null) { ShowViewerMessage("The viewer isn't ready yet. Try again in a moment."); return; }
            try
            {
                await EnsureViewer();
                _viewerText = asText ? path : null;
                if (asText)
                {
                    var fi = new FileInfo(path);
                    string text = fi.Length > 1_500_000 ? "(File is too large to preview.)" : File.ReadAllText(path);
                    string html = "<!doctype html><meta charset='utf-8'><style>body{margin:0;" + (Theme.IsLight ? "background:#ffffff;color:#1b1f29;" : "background:#0f1115;color:#e6e8ee;") +
                                  "font:13px/1.55 'Cascadia Mono',Consolas,monospace}pre{padding:20px;white-space:pre-wrap;word-break:break-word}</style><pre>" +
                                  WebUtility.HtmlEncode(text) + "</pre>";
                    _viewer.CoreWebView2.NavigateToString(html);
                }
                else
                {
                    string rel = Path.GetRelativePath(_root, path);
                    string url = "https://study.local/" + string.Join("/", rel.Split('\\').Select(Uri.EscapeDataString));
                    if (officeType != null)
                        url = "https://app.local/office.html?type=" + officeType + "&src=" + Uri.EscapeDataString(url);
                    _viewer.CoreWebView2.Navigate(url);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowViewerMessage("Couldn't open this file: " + ex.Message);
            }
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        // =====================================================================
        // Notes: a side panel available on every page, saved into the study folder
        // =====================================================================

        string _noteFile;
        bool _noteDirty, _noteLoading;
        DispatcherTimer _noteSaveTimer;
        double _notesWidth = 400;

        void InitNotes()
        {
            _noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _noteSaveTimer.Tick += (_, __) => { _noteSaveTimer.Stop(); SaveNote(); };
            // Today's session note; the file is only created once something is typed.
            _noteFile = Path.Combine(_root, "Notes", $"Session notes {DateTime.Now:yyyy-MM-dd}.md");
            LoadNoteText(_noteFile);
        }

        void LoadNoteText(string path)
        {
            _noteLoading = true;
            try { NotesBox.Text = File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (Exception ex) { NotesBox.Text = ""; ShowBanner("Couldn't open note: " + ex.Message); }
            finally { _noteLoading = false; }
            _noteFile = path;
            _noteDirty = false;
            NoteName.Text = Path.GetRelativePath(_root, path);
            NoteStatus.Text = File.Exists(path) ? "Saved in your study folder" : "Start typing. Notes save automatically.";
        }

        void ShowNotes(bool show)
        {
            if (!show && NotesPanel.Visibility == Visibility.Visible && NotesColumn.ActualWidth > 150)
                _notesWidth = NotesColumn.ActualWidth;
            NotesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            NotesSplitter.Visibility = NotesPanel.Visibility;
            NotesColumn.Width = show ? new GridLength(_notesWidth) : new GridLength(0);
            NotesColumn.MinWidth = show ? 260 : 0;
            StyleToggle(NotesToggle, show);
            if (show) FocusLater(NotesBox);
            else SaveNote();
        }

        void NotesToggle_Click(object sender, RoutedEventArgs e) => ShowNotes(NotesPanel.Visibility != Visibility.Visible);

        void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_noteLoading) return;
            _noteDirty = true;
            NoteStatus.Text = "Editing…";
            _noteSaveTimer?.Stop();
            _noteSaveTimer?.Start();
        }

        void SaveNote()
        {
            if (!_noteDirty || _noteFile == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_noteFile));
                File.WriteAllText(_noteFile, NotesBox.Text);
                _noteDirty = false;
                NoteStatus.Text = $"Saved  ·  {NoteName.Text}";
            }
            catch (Exception ex)
            {
                NoteStatus.Text = "Couldn't save: " + ex.Message;
            }
        }

        void OpenNote(string path)
        {
            if (!IsInsideRoot(path)) return;
            SaveNote();
            LoadNoteText(path);
            NoteListPanel.Visibility = Visibility.Collapsed;
            ShowNotes(true);
        }

        void NoteNew_Click(object sender, RoutedEventArgs e)
        {
            SaveNote();
            string folder = Path.Combine(_root, "Notes");
            // Default name carries the date only: no clock time anywhere during a session.
            string suggestion = Path.GetFileName(UniquePath(Path.Combine(folder, $"Note {DateTime.Now:yyyy-MM-dd}.md")));
            Prompt("New note", suggestion, n =>
            {
                var err = ValidateName(n);
                if (err != null) return err;
                if (!Path.HasExtension(n)) n += ".md";
                return File.Exists(Path.Combine(folder, n)) ? "A note with that name already exists." : null;
            }, n =>
            {
                if (!Path.HasExtension(n)) n += ".md";
                var path = Path.Combine(folder, n);
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(path, "# " + Path.GetFileNameWithoutExtension(n) + Environment.NewLine + Environment.NewLine);
                    OpenNote(path);
                    NotesBox.CaretIndex = NotesBox.Text.Length;
                    if (FilesPage.Visibility == Visibility.Visible) LoadFolder(_dir);
                }
                catch (Exception ex) { ShowBanner(ex.Message); }
            });
        }

        void NoteOpen_Click(object sender, RoutedEventArgs e)
        {
            SaveNote();
            var notes = EnumerateStudyFiles()
                .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(p => new PickItem { Full = p, Rel = Path.GetRelativePath(_root, p) })
                .ToList();
            NoteList.ItemsSource = notes;
            NoteListEmpty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoteListPanel.Visibility = Visibility.Visible;
            FocusLater(NoteList);
        }

        void NoteListCancel_Click(object sender, RoutedEventArgs e) => NoteListPanel.Visibility = Visibility.Collapsed;

        void NoteList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (NoteList.SelectedItem is PickItem p) OpenNote(p.Full);
        }

        void NoteList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && NoteList.SelectedItem is PickItem p) { OpenNote(p.Full); e.Handled = true; }
            else if (e.Key == Key.Escape) { NoteListPanel.Visibility = Visibility.Collapsed; e.Handled = true; }
        }

        /// <summary>Adds the current web page (title + link) to the note, so sources are never lost.</summary>
        void NoteClip_Click(object sender, RoutedEventArgs e)
        {
            var core = _cur?.Core;
            if (core == null || string.IsNullOrEmpty(core.Source) || core.Source.StartsWith("about:"))
            {
                ShowBanner("Open a web page in the Browser first, then clip it.");
                return;
            }
            string title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle.Trim();
            string line = $"- [{title.Replace("[", "(").Replace("]", ")")}]({core.Source})";
            string text = NotesBox.Text;
            string prefix = text.Length == 0 || text.EndsWith("\n") ? "" : Environment.NewLine;
            NotesBox.AppendText(prefix + line + Environment.NewLine);
            NotesBox.CaretIndex = NotesBox.Text.Length;
            NotesBox.ScrollToEnd();
            FocusLater(NotesBox);
        }

        // =====================================================================
        // Code
        // =====================================================================

        const string PythonTemplate =
            "# Python. Press F5 or Run.\r\n" +
            "# Relative paths start in your study folder; plt.show() figures appear on the right.\r\n" +
            "import math\r\n\r\n" +
            "print(\"Hello, focus!\", math.pi)\r\n";

        const string OctaveTemplate =
            "% MATLAB code. Press F5 or Run. Plots appear on the right.\r\n" +
            "% Relative paths start in your study folder.\r\n" +
            "x = linspace(0, 2*pi, 200);\r\n" +
            "y = sin(x);\r\n" +
            "plot(x, y); grid on; title('sin(x)');\r\n" +
            "disp('Done');\r\n";

        static string CodeTemplate(string lang) =>
            lang == "python" ? PythonTemplate : lang == "octave" ? OctaveTemplate : "";

        static string DefaultExt(string lang) => lang == "python" ? ".py" : lang == "octave" ? ".m" : ".md";

        void InitCode()
        {
            _runner.Output += line => Dispatcher.BeginInvoke(new Action(() => AppendOutput(line)));
            _runner.Figure += p => Dispatcher.BeginInvoke(new Action(() => AddFigure(p)));
            _runner.Exited += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                RunBtn.IsEnabled = _lang != "text";
                StopBtn.IsEnabled = false;
                CodeStatus.Text = code == 0 ? "Finished" : $"Stopped (exit code {code})";
            }));
            _mEngine = CodeRunner.MEngine(_s);
            _runner.WarmUp(_s); // start MATLAB in the background now so it's ready when needed
            LangOctave.Content = _mEngine == "octave" ? "MATLAB (Octave)" : "MATLAB";
            SetLang("python");
            _loadingCode = true;
            Editor.Text = PythonTemplate;
            _loadingCode = false;
            _dirty = false;
            UpdateCodeLabel();
        }

        void SetLang(string lang)
        {
            _lang = lang;
            StyleToggle(LangPython, lang == "python");
            StyleToggle(LangOctave, lang == "octave");
            bool code = lang != "text";
            RunBtn.IsEnabled = code && !_runner.IsRunning;
            OutputPane.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
            CodeSplitter.Visibility = OutputPane.Visibility;
            OutputColumn.Width = code ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            Editor.FontFamily = code ? (FontFamily)FindResource("Mono") : new FontFamily("Segoe UI");
            Editor.FontSize = code ? 14 : 15;
            Editor.TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap;
        }

        void Lang_Click(object sender, RoutedEventArgs e)
        {
            string lang = (string)((Button)sender).Tag;
            if (lang == _lang) return;
            // switching language on an untouched template swaps the template too
            bool untouched = !_dirty && _codeFile == null;
            SetLang(lang);
            if (untouched)
            {
                _loadingCode = true;
                Editor.Text = CodeTemplate(lang);
                _loadingCode = false;
            }
            UpdateCodeLabel();
        }

        void UpdateCodeLabel()
        {
            string name = _codeFile == null ? "untitled" + DefaultExt(_lang) : Path.GetRelativePath(_root, _codeFile);
            CodeFileLabel.Text = name + (_dirty ? "  •  unsaved" : "");
        }

        void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingCode) return;
            if (!_dirty) { _dirty = true; UpdateCodeLabel(); }
        }

        void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
            {
                InsertAtCaret("    ");
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _lang != "text")
            {
                int ci = Editor.SelectionStart;
                string text = Editor.Text;
                int ls = ci == 0 ? -1 : text.LastIndexOf('\n', ci - 1);
                string line = text.Substring(ls + 1, ci - (ls + 1));
                string indent = new string(line.TakeWhile(ch => ch == ' ' || ch == '\t').ToArray());
                string trimmed = line.TrimEnd();
                if (_lang == "python" && trimmed.EndsWith(":")) indent += "    ";
                InsertAtCaret("\r\n" + indent);
                e.Handled = true;
            }
        }

        void InsertAtCaret(string s)
        {
            int start = Editor.SelectionStart;
            Editor.SelectedText = s;
            Editor.Select(start + s.Length, 0);
        }

        void OpenInCode(string path)
        {
            if (!IsInsideRoot(path)) return;
            AutoSaveCode();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                _loadingCode = true;
                Editor.Text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                ShowBanner("Couldn't open file: " + ex.Message);
                return;
            }
            finally
            {
                _loadingCode = false;
            }
            _codeFile = path;
            _dirty = false;
            SetLang(ext == ".py" ? "python" : ext == ".m" ? "octave" : "text");
            UpdateCodeLabel();
            ShowPage("code");
            Editor.Focus();
        }

        /// <summary>Never lose work: saves the open file, or untitled edits into the study folder.</summary>
        void AutoSaveCode()
        {
            if (!_dirty) return;
            try
            {
                if (_codeFile != null)
                {
                    File.WriteAllText(_codeFile, Editor.Text);
                }
                else if (Editor.Text.Trim().Length > 0 && Editor.Text != CodeTemplate(_lang))
                {
                    _codeFile = UniquePath(Path.Combine(_root, "untitled" + DefaultExt(_lang))); // no time in the name
                    File.WriteAllText(_codeFile, Editor.Text);
                    ShowBanner("Unsaved code was saved as " + Path.GetFileName(_codeFile));
                }
                _dirty = false;
                UpdateCodeLabel();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        void CodeNew_Click(object sender, RoutedEventArgs e)
        {
            AutoSaveCode();
            _codeFile = null;
            _loadingCode = true;
            Editor.Text = CodeTemplate(_lang);
            _loadingCode = false;
            _dirty = false;
            UpdateCodeLabel();
            Editor.Focus();
        }

        void CodeSave_Click(object sender, RoutedEventArgs e)
        {
            if (_codeFile == null) { CodeSaveAs_Click(sender, e); return; }
            try
            {
                File.WriteAllText(_codeFile, Editor.Text);
                _dirty = false;
                UpdateCodeLabel();
                CodeStatus.Text = "Saved";
            }
            catch (Exception ex) { ShowBanner("Save failed: " + ex.Message); }
        }

        void CodeSaveAs_Click(object sender, RoutedEventArgs e)
        {
            string ext = DefaultExt(_lang);
            string suggestion = _codeFile != null ? Path.GetFileName(_codeFile) : (_lang == "text" ? "notes" : "script") + ext;
            string folder = _dir ?? _root;
            Prompt("Save in your study folder as", suggestion, n =>
            {
                var err = ValidateName(n);
                if (err != null) return err;
                if (!Path.HasExtension(n)) n += ext;
                return File.Exists(Path.Combine(folder, n)) ? "A file with that name already exists in this folder." : null;
            }, n =>
            {
                if (!Path.HasExtension(n)) n += ext;
                _codeFile = Path.Combine(folder, n);
                CodeSave_Click(null, null);
                if (FilesPage.Visibility == Visibility.Visible) LoadFolder(_dir);
            });
        }

        void Run_Click(object sender, RoutedEventArgs e)
        {
            if (_lang == "text" || _runner.IsRunning) return;
            if (_codeFile != null && _dirty) CodeSave_Click(null, null);

            OutputBox.Clear();
            FiguresPanel.Children.Clear();
            FiguresScroll.Visibility = Visibility.Collapsed;
            string workDir = _codeFile != null ? Path.GetDirectoryName(_codeFile) : _root;
            string err = _runner.Start(_lang, Editor.Text, workDir, StdinBox.Text, _s);
            if (err != null)
            {
                OutputBox.Text = err;
                CodeStatus.Text = "Couldn't run";
                return;
            }
            RunBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            CodeStatus.Text = _lang == "octave" && _mEngine == "matlab" && !_runner.MatlabReady
                ? "Starting MATLAB, the first run takes up to a minute…"
                : "Running…";
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _runner.Stop();

        void ClearOutput_Click(object sender, RoutedEventArgs e)
        {
            OutputBox.Clear();
            FiguresPanel.Children.Clear();
            FiguresScroll.Visibility = Visibility.Collapsed;
        }

        void AppendOutput(string line)
        {
            OutputBox.AppendText(line + "\n");
            if (OutputBox.Text.Length > 200_000) OutputBox.Text = OutputBox.Text.Substring(OutputBox.Text.Length - 150_000);
            OutputBox.ScrollToEnd();
        }

        void AddFigure(string path)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                FiguresPanel.Children.Add(new Border
                {
                    Background = Brushes.White, CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Margin = new Thickness(6),
                    Child = new Image { Source = bi, Stretch = Stretch.Uniform, MaxHeight = 340 }
                });
                FiguresScroll.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                AppendOutput("Could not display figure: " + ex.Message);
            }
        }

        // =====================================================================
        // Development aid: render each screen to PNG (FocusStudy.exe --snap <dir>)
        // =====================================================================

        public static void Snap(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            int w = Math.Max(1, (int)el.ActualWidth), h = Math.Max(1, (int)el.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.Resources["Bg"], null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        async Task RunSnapshots()
        {
            async Task Shot(string name)
            {
                await Task.Delay(700);
                Snap(RootGrid, Path.Combine(_snapDir, name));
            }
            try
            {
                File.WriteAllText(Path.Combine(_root, "lecture-notes.md"), "# Notes\n");
                File.WriteAllText(Path.Combine(_root, "solver.py"), "print(1)\n");
                Directory.CreateDirectory(Path.Combine(_root, "Physics"));
                LoadFolder(_root);
                await Shot("1_browser.png");
                ShowPage("files"); await Shot("2_files.png");
                string samples = Environment.GetEnvironmentVariable("FOCUS_SNAP_SAMPLES");
                if (!string.IsNullOrEmpty(samples) && Directory.Exists(samples))
                {
                    _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_snapDir, "wv2data"));
                    foreach (var f in Directory.GetFiles(samples))
                    {
                        string dest = Path.Combine(_root, Path.GetFileName(f));
                        File.Copy(f, dest, true);
                        string ext = Path.GetExtension(f);
                        await OpenInViewer(dest, false, ext.TrimStart('.').ToLowerInvariant());
                        await Task.Delay(4000);
                        using (var fs = File.Create(Path.Combine(_snapDir, "office_" + Path.GetFileName(f) + ".png")))
                            await _viewer.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                    }
                    ShowNotes(true);
                    NotesBox.Text = "# Heat transfer\r\n\r\n- Fourier's law: q = -k dT/dx\r\n- Check sign convention\r\n";
                    await Shot("2c_notes.png");
                    ShowNotes(false);
                }
                ShowPage("code"); Run_Click(null, null); await Task.Delay(2500); await Shot("3_code.png"); string keep = _s.Theme; Theme_Click(null, null); await Shot("3c_code_switched.png"); Theme.Apply(keep); _s.Theme = keep; try { _s.Save(); } catch { }
                if (Environment.GetEnvironmentVariable("FOCUS_SNAP_MATLAB") == "1")
                {
                    SetLang("octave"); _loadingCode = true; Editor.Text = CodeTemplate("octave"); _loadingCode = false;
                    var t0 = Stopwatch.StartNew();
                    Run_Click(null, null);
                    for (int i = 0; i < 480 && StopBtn.IsEnabled; i++) await Task.Delay(500);
                    File.AppendAllText(Path.Combine(_snapDir, "timing.txt"), $"first run: {t0.Elapsed.TotalSeconds:F1} s\n" + OutputBox.Text + "\n");
                    _loadingCode = true; Editor.Text = "a = magic(4)\r\nbar(sum(a)); title('second run');\r\n"; _loadingCode = false;
                    t0.Restart();
                    Run_Click(null, null);
                    for (int i = 0; i < 240 && StopBtn.IsEnabled; i++) await Task.Delay(250);
                    File.AppendAllText(Path.Combine(_snapDir, "timing.txt"), $"second run: {t0.Elapsed.TotalSeconds:F1} s\n" + OutputBox.Text + "\n");
                    await Shot("3b_matlab.png");
                }
                RequestExit(); await Shot("4_exit_step1.png");
                _exitStep = 4; ShowExitStep(); await Shot("5_exit_cooldown.png");
                ExitStay_Click(null, null);
                _flagTab = null; DistractTitle.Text = "Funny cat compilation 2026 - YouTube"; DistractReason.Text = "The page title mentions “funny”, which doesn't look like study material.";
                ShowOverlay(DistractCard); await Shot("6_offtopic.png");
                HideOverlay(); EndSession(true); await Shot("7_summary.png");
                Continue_Click(new Button { Tag = "15" }, null); ShowPage("browser"); await Shot("8_continued.png");
                File.WriteAllText(Path.Combine(_snapDir, "continue.txt"), $"ended={_ended} block={_block} planned={_planned} overlay={(_overlay == null ? "none" : _overlay.Name)}");
                EndSession(true); await Shot("9_summary_extra.png");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(_snapDir, "error.txt"), ex.ToString());
            }
            Unlock();
        }
    }
}
