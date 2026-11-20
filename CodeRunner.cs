using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FocusStudy
{
    /// <summary>Runs Python or GNU Octave (MATLAB-compatible) code in a hidden child process.</summary>
    internal sealed class CodeRunner
    {
        public const string FigureMarker = "@@FOCUSFIG@@";

        Process _p;

        public bool IsRunning
        {
            get
            {
                if (_mlBusy) return true;
                try { return _p != null && !_p.HasExited; } catch { return false; }
            }
        }

        public event Action<string> Output;
        public event Action<string> Figure;
        public event Action<int> Exited;

        // ---------- interpreter discovery ----------

        public static string FindPython(Settings s)
        {
            if (!string.IsNullOrWhiteSpace(s.PythonPath) && File.Exists(s.PythonPath)) return s.PythonPath;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir) || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), "python.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"C:\"
            };
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root, "Python3*").OrderByDescending(x => x))
                    {
                        var p = Path.Combine(d, "python.exe");
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            var py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
            return File.Exists(py) ? py : null;
        }

        public static string FindOctave(Settings s)
        {
            if (!string.IsNullOrWhiteSpace(s.OctavePath) && File.Exists(s.OctavePath)) return s.OctavePath;
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GNU Octave"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GNU Octave"),
                @"C:\Octave"
            };
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x))
                    {
                        foreach (var rel in new[] { @"mingw64\bin\octave-cli.exe", @"bin\octave-cli.exe" })
                        {
                            var p = Path.Combine(d, rel);
                            if (File.Exists(p)) return p;
                        }
                    }
                }
                catch { }
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim().Trim('"'), "octave-cli.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        public static string FindMatlab(Settings s)
        {
            if (!string.IsNullOrWhiteSpace(s.MatlabPath) && File.Exists(s.MatlabPath)) return s.MatlabPath;
            foreach (var root in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MATLAB"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MATLAB")
            })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root, "R*").OrderByDescending(x => x))
                    {
                        var p = Path.Combine(d, "bin", "matlab.exe");
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim().Trim('"'), "matlab.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Which engine runs .m code: "matlab", "octave", or null if neither is installed.</summary>
        public static string MEngine(Settings s)
        {
            string pref = (s.MatlabEngine ?? "auto").Trim().ToLowerInvariant();
            bool hasMatlab = FindMatlab(s) != null, hasOctave = FindOctave(s) != null;
            if (pref == "octave" && hasOctave) return "octave";
            if (pref == "matlab" && hasMatlab) return "matlab";
            if (hasMatlab) return "matlab";
            if (hasOctave) return "octave";
            return null;
        }

        // ---------- running ----------

        /// <summary>Starts the code. Returns null on success or an error message.</summary>
        public string Start(string lang, string code, string workDir, string stdin, Settings s)
        {
            if (IsRunning) return "Code is already running.";

            string runDir = Path.Combine(Settings.LocalDir, "run", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(runDir);
            CleanOldRuns(runDir);

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir
            };

            if (lang == "python")
            {
                string exe = FindPython(s);
                if (exe == null)
                    return "Python was not found.\nInstall it (free) from python.org before your session, or set \"PythonPath\" in:\n" + Settings.FilePath;
                string script = Path.Combine(runDir, "main.py");
                string runner = Path.Combine(runDir, "focus_runner.py");
                File.WriteAllText(script, code, new UTF8Encoding(false));
                File.WriteAllText(runner, PythonRunner, new UTF8Encoding(false));
                psi.FileName = exe;
                string pre = Path.GetFileName(exe).Equals("py.exe", StringComparison.OrdinalIgnoreCase) ? "-3 " : "";
                psi.Arguments = $"{pre}-u \"{runner}\" \"{script}\" \"{runDir}\"";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONUNBUFFERED"] = "1";
                psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                psi.Environment["MPLBACKEND"] = "Agg";
            }
            else if (lang == "octave" && MEngine(s) == "matlab")
            {
                // Installed MATLAB: hand the job to the warm background MATLAB (no desktop window).
                string script = Path.Combine(runDir, "focus_main.m");
                string wrapper = Path.Combine(runDir, "focus_wrapper.m");
                File.WriteAllText(script, code, new UTF8Encoding(false));
                File.WriteAllText(wrapper, MatlabWrapper(workDir, runDir), new UTF8Encoding(false));
                string err = EnsureMatlab(s);
                if (err != null) return err;
                _mlBusy = true;
                try
                {
                    string tmp = Path.Combine(_mlDir, "job.tmp");
                    File.WriteAllText(tmp, runDir, new UTF8Encoding(false));
                    File.Move(tmp, Path.Combine(_mlDir, "job.txt"), true);
                }
                catch (Exception ex)
                {
                    _mlBusy = false;
                    return "Could not send the job to MATLAB: " + ex.Message;
                }
                return null;
            }
            else if (lang == "octave")
            {
                string exe = FindOctave(s);
                if (exe == null)
                    return "Neither MATLAB nor GNU Octave was found.\nInstall MATLAB, or Octave (free, MATLAB-compatible) from octave.org/download, before your session.\n" +
                           "You can also set \"MatlabPath\" or \"OctavePath\" in:\n" + Settings.FilePath;
                string script = Path.Combine(runDir, "focus_main.m");
                string wrapper = Path.Combine(runDir, "focus_wrapper.m");
                File.WriteAllText(script, code, new UTF8Encoding(false));
                File.WriteAllText(wrapper, OctaveWrapper(script, workDir, runDir), new UTF8Encoding(false));
                psi.FileName = exe;
                psi.Arguments = $"-q -f --no-history \"{wrapper}\"";
            }
            else
            {
                return "Notes can't be run.";
            }

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
                p.Start();
                _p = p;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                try
                {
                    if (!string.IsNullOrEmpty(stdin)) p.StandardInput.Write(stdin.Replace("\r\n", "\n") + "\n");
                    p.StandardInput.Close();
                }
                catch { }
                Task.Run(() =>
                {
                    int codeOut = -1;
                    try { p.WaitForExit(); codeOut = p.ExitCode; } catch { }
                    Exited?.Invoke(codeOut);
                });
                return null;
            }
            catch (Exception ex)
            {
                return "Could not start the interpreter: " + ex.Message;
            }
        }

        void HandleLine(string line)
        {
            if (line.StartsWith(FigureMarker)) Figure?.Invoke(line.Substring(FigureMarker.Length).Trim());
            else Output?.Invoke(line);
        }

        public void Stop()
        {
            try { if (_p != null && !_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
            // A running MATLAB job can't be interrupted from outside, so restart the background MATLAB.
            if (_mlBusy) KillMatlab();
        }

        // ---------- warm background MATLAB ----------
        // Starting MATLAB takes ~30-60 s, so one headless MATLAB is kept running for the session and
        // receives jobs through a folder. It quits by itself if this app stops sending heartbeats.

        Process _ml;
        string _mlDir;
        volatile bool _mlBusy, _mlReady;
        System.Threading.Timer _heartbeat;

        public bool MatlabReady => _mlReady;

        /// <summary>Starts MATLAB in the background (if it's the engine) so the first run is fast.</summary>
        public void WarmUp(Settings s)
        {
            if (MEngine(s) == "matlab") EnsureMatlab(s);
        }

        string EnsureMatlab(Settings s)
        {
            if (_ml != null) return null;
            string exe = FindMatlab(s);
            if (exe == null) return "MATLAB was not found.";
            try
            {
                _mlReady = false;
                _mlDir = Path.Combine(Settings.LocalDir, "matlab", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_mlDir);
                CleanOldRuns(_mlDir);
                File.WriteAllText(Path.Combine(_mlDir, "focus_server.m"), MatlabServer, new UTF8Encoding(false));
                Beat();
                _heartbeat ??= new System.Threading.Timer(_ => Beat(), null, 5000, 5000);

                var psi = new ProcessStartInfo(exe, $"-batch \"addpath('{Oct(_mlDir)}'); focus_server('{Oct(_mlDir)}')\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = _mlDir
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("@@FOCUSREADY@@")) { _mlReady = true; return; }
                    if (e.Data.StartsWith("@@FOCUSDONE@@")) { _mlBusy = false; Exited?.Invoke(0); return; }
                    if (_mlBusy) HandleLine(e.Data);
                };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null && _mlBusy) Output?.Invoke(e.Data); };
                p.Exited += (_, __) =>
                {
                    if (_ml != p) return;
                    _ml = null;
                    _mlReady = false;
                    if (_mlBusy) { _mlBusy = false; Exited?.Invoke(-1); }
                };
                p.Start();
                _ml = p;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return null;
            }
            catch (Exception ex)
            {
                _ml = null;
                return "Could not start MATLAB: " + ex.Message;
            }
        }

        void Beat()
        {
            try { if (_mlDir != null) File.WriteAllText(Path.Combine(_mlDir, "heartbeat.txt"), DateTime.UtcNow.ToString("o")); } catch { }
        }

        void KillMatlab()
        {
            var p = _ml;
            if (p == null) return;
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        /// <summary>Call when the session ends: stops the background MATLAB.</summary>
        public void Shutdown()
        {
            Stop();
            try { if (_mlDir != null) File.WriteAllText(Path.Combine(_mlDir, "quit.txt"), "quit"); } catch { }
            var p = _ml;
            _ml = null;
            _heartbeat?.Dispose();
            _heartbeat = null;
            try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        const string MatlabServer = @"function focus_server(jobdir)
set(0, 'DefaultFigureVisible', 'off');
jobFile = fullfile(jobdir, 'job.txt');
quitFile = fullfile(jobdir, 'quit.txt');
hbFile = fullfile(jobdir, 'heartbeat.txt');
fprintf('@@FOCUSREADY@@\n');
while ~exist(quitFile, 'file')
    hb = dir(hbFile);
    if isempty(hb) || (now - hb.datenum) > 45/86400
        break;  % the study app is gone: don't keep MATLAB (and its licence) busy
    end
    if exist(jobFile, 'file')
        runDir = strtrim(fileread(jobFile));
        delete(jobFile);
        focus_job(runDir);
        fprintf('@@FOCUSDONE@@\n');
    else
        pause(0.15);
    end
end
end

function focus_job(focusRunDir)
close all force;
try
    run(fullfile(focusRunDir, 'focus_wrapper.m'));
catch focusErr
    fprintf(2, '%s\n', getReport(focusErr, 'basic'));
end
close all force;
end
";

        static void CleanOldRuns(string keep)
        {
            try
            {
                var parent = Path.GetDirectoryName(keep);
                foreach (var d in Directory.GetDirectories(parent).OrderByDescending(x => x).Skip(10))
                    try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        static string Oct(string s) => s.Replace("'", "''");

        static string OctaveWrapper(string script, string workDir, string figDir) =>
$@"set(0, 'defaultfigurevisible', 'off');
cd('{Oct(workDir)}');
try
  source('{Oct(script)}');
catch err
  fprintf(2, 'error: %s\n', err.message);
end
figs = sort(findall(0, 'type', 'figure'));
for i = 1:numel(figs)
  p = fullfile('{Oct(figDir)}', sprintf('fig_%d.png', i));
  try
    print(figs(i), p, '-dpng', '-r110');
    printf('{FigureMarker}%s\n', p);
  catch perr
    fprintf(2, 'Could not render figure %d: %s\n', i, perr.message);
  end
end
";

        // The user's script sits in runDir (added to the path) and is called by name, so it runs in the
        // study folder without MATLAB changing directory, and local functions in the script still work.
        static string MatlabWrapper(string workDir, string figDir) =>
$@"set(0, 'DefaultFigureVisible', 'off');
clear focus_main;
addpath('{Oct(figDir)}');
rehash;
cd('{Oct(workDir)}');
try
    focus_main;
catch focusErr
    fprintf(2, '%s\n', getReport(focusErr, 'basic'));
end
rmpath('{Oct(figDir)}');
focusFigs = findall(groot, 'Type', 'figure');
if ~isempty(focusFigs)
    [~, focusOrder] = sort([focusFigs.Number]);
    focusFigs = focusFigs(focusOrder);
end
for focusI = 1:numel(focusFigs)
    focusP = fullfile('{Oct(figDir)}', sprintf('fig_%d.png', focusI));
    try
        if exist('exportgraphics', 'file')
            exportgraphics(focusFigs(focusI), focusP, 'Resolution', 110);
        else
            print(focusFigs(focusI), focusP, '-dpng', '-r110');
        end
        fprintf('{FigureMarker}%s\n', focusP);
    catch focusPErr
        fprintf(2, 'Could not render figure %d: %s\n', focusI, focusPErr.message);
    end
end
";

        const string PythonRunner = @"import sys, os, runpy, warnings, builtins
warnings.filterwarnings('ignore', message='.*non-interactive.*')
warnings.filterwarnings('ignore', message='.*FigureCanvasAgg.*')
script, figdir = sys.argv[1], sys.argv[2]
sys.argv = [script]
sys.path.insert(0, os.getcwd())
_count = [0]

def _save_figs(*args, **kwargs):
    plt = sys.modules.get('matplotlib.pyplot')
    if plt is None:
        return
    for num in plt.get_fignums():
        _count[0] += 1
        path = os.path.join(figdir, 'fig_%d.png' % _count[0])
        try:
            plt.figure(num).savefig(path, dpi=110, bbox_inches='tight')
            print('@@FOCUSFIG@@' + path, flush=True)
        except Exception as e:
            print('Could not render figure:', e, file=sys.stderr)
    plt.close('all')

_orig_import = builtins.__import__
def _hook(name, *a, **k):
    m = _orig_import(name, *a, **k)
    plt = sys.modules.get('matplotlib.pyplot')
    if plt is not None and not getattr(plt, '_focus_patched', False):
        plt._focus_patched = True
        plt.show = _save_figs
    return m
builtins.__import__ = _hook

try:
    runpy.run_path(script, run_name='__main__')
finally:
    builtins.__import__ = _orig_import
    _save_figs()
";
    }
}
