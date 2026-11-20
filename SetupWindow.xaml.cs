using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FocusStudy
{
    public partial class SetupWindow : Window
    {
        readonly Settings _s;
        readonly bool _demo;
        readonly string _snap;
        bool _started;

        public SetupWindow(bool demo, string snap)
        {
            InitializeComponent();
            _s = Settings.Load();
            _demo = demo;
            _snap = snap;

            HoursBox.Text = (_s.SessionMinutes / 60).ToString();
            MinutesBox.Text = (_s.SessionMinutes % 60).ToString();
            FolderBox.Text = _s.StudyFolder;
            IdleBox.Text = _s.IdleMinutes.ToString();
            BlockedBox.Text = string.Join(Environment.NewLine, _s.BlockedDomains);

            var py = CodeRunner.FindPython(_s);
            PythonStatus.Text = py != null ? $"✓  Python ready  ({py})" : "✗  Python not found. Install it free from python.org.";
            PythonStatus.Foreground = (System.Windows.Media.Brush)FindResource(py != null ? "Good" : "Warn");
            string engine = CodeRunner.MEngine(_s);
            if (engine == "matlab")
                OctaveStatus.Text = $"✓  MATLAB ready, using your installed copy  ({CodeRunner.FindMatlab(_s)})";
            else if (engine == "octave")
                OctaveStatus.Text = $"✓  MATLAB-compatible GNU Octave ready  ({CodeRunner.FindOctave(_s)})";
            else
                OctaveStatus.Text = "✗  No MATLAB or GNU Octave found. Octave is free and runs most MATLAB code. Install one before your session.";
            OctaveStatus.Foreground = (System.Windows.Media.Brush)FindResource(engine != null ? "Good" : "Warn");
            GetOctaveBtn.Visibility = engine != null ? Visibility.Collapsed : Visibility.Visible;

            if (_demo)
            {
                Title = "Focus Study (demo, no lock)";
                StartBtn.Content = "Start demo session (no lock)";
            }

            Closed += (_, __) => { if (!_started) Application.Current.Shutdown(); };

            if (_snap != null) Loaded += (_, __) => RunSnapshot();
        }

        void Preset_Click(object sender, RoutedEventArgs e)
        {
            int m = int.Parse((string)((Button)sender).Tag);
            HoursBox.Text = (m / 60).ToString();
            MinutesBox.Text = (m % 60).ToString();
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your study folder" };
            if (Directory.Exists(FolderBox.Text)) d.InitialDirectory = FolderBox.Text;
            if (d.ShowDialog(this) == true) FolderBox.Text = d.FolderName;
        }

        void GetOctave_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://octave.org/download") { UseShellExecute = true }); } catch { }
        }

        static string CheckFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "Choose a study folder.";
            if (!Directory.Exists(folder)) return "That study folder doesn't exist.";
            string full = Path.GetFullPath(folder).TrimEnd('\\');
            if (string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
                return "Pick a specific folder, not a whole drive.";
            string[] tooBroad =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (tooBroad.Any(p => !string.IsNullOrEmpty(p) && string.Equals(p.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)))
                return "That folder is too broad. Pick (or create) a folder just for study material.";
            return null;
        }

        bool Collect(out int minutes)
        {
            minutes = 0;
            ErrorText.Text = "";
            if (!int.TryParse(HoursBox.Text.Trim() == "" ? "0" : HoursBox.Text.Trim(), out int h) ||
                !int.TryParse(MinutesBox.Text.Trim() == "" ? "0" : MinutesBox.Text.Trim(), out int m) || h < 0 || m < 0)
            {
                ErrorText.Text = "Enter the session length as whole hours and minutes.";
                return false;
            }
            minutes = h * 60 + m;
            if (minutes < 1 || minutes > 12 * 60)
            {
                ErrorText.Text = "Session length must be between 1 minute and 12 hours.";
                return false;
            }
            var folderErr = CheckFolder(FolderBox.Text);
            if (folderErr != null) { ErrorText.Text = folderErr; return false; }
            if (!int.TryParse(IdleBox.Text.Trim(), out int idle) || idle < 1 || idle > 60)
            {
                ErrorText.Text = "Idle nudge must be between 1 and 60 minutes.";
                return false;
            }

            _s.SessionMinutes = minutes;
            _s.StudyFolder = Path.GetFullPath(FolderBox.Text);
            _s.IdleMinutes = idle;
            _s.BlockedDomains = BlockedBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(FocusMonitor.NormalizeDomain)
                .Where(d => d.Length > 0)
                .Distinct()
                .ToList();
            try { _s.Save(); } catch { }
            return true;
        }

        void Start_Click(object sender, RoutedEventArgs e)
        {
            if (!Collect(out int minutes)) return;

            if (!_demo)
            {
                string len = minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
                var r = MessageBox.Show(this,
                    $"Your PC will be locked into Focus Study for {len}.\n\n" +
                    "• Alt+F4 starts the steps to leave early.\n" +
                    "• Ctrl+Alt+Del stays available for emergencies.\n\n" +
                    "Save your work in other apps first. Start now?",
                    "Start focus session", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (r != MessageBoxResult.OK) return;
            }

            Launch(TimeSpan.FromMinutes(minutes));
        }

        void Launch(TimeSpan length)
        {
            _started = true;
            new SessionWindow(_s, length, _demo, _snap).Show();
            Close();
        }

        // ---- headless layout check used during development: FocusStudy.exe --snap <dir> ----
        void RunSnapshot()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            t.Tick += (_, __) =>
            {
                t.Stop();
                Directory.CreateDirectory(_snap);
                SessionWindow.Snap((FrameworkElement)Content, Path.Combine(_snap, "0_setup.png"));
                string study = Path.Combine(_snap, "Study");
                Directory.CreateDirectory(study);
                _s.StudyFolder = study;
                Launch(TimeSpan.FromMinutes(5));
            };
            t.Start();
        }
    }
}
