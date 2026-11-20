using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace FocusStudy
{
    public partial class App : Application
    {
        /// <summary>Set when Windows is signing out / shutting down: every window must be allowed to close.</summary>
        public static bool ForceClose;

        Mutex _single;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _single = new Mutex(true, "FocusStudy.SingleInstance", out bool first);
            if (!first) { Shutdown(); return; }

            DispatcherUnhandledException += (_, a) => { LogError(a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            {
                KeyboardLock.ReleaseAll();
                LogError(a.ExceptionObject as Exception);
            };
            SessionEnding += (_, __) =>
            {
                ForceClose = true;
                KeyboardLock.ReleaseAll();
                foreach (var w in Windows.OfType<SessionWindow>()) w.SaveForShutdown();
            };

            var args = e.Args;
            bool demo = args.Contains("--demo");
            string snap = null;
            int i = Array.IndexOf(args, "--snap");
            if (i >= 0 && i + 1 < args.Length) { snap = args[i + 1]; demo = true; }

            new SetupWindow(demo, snap).Show();
        }

        public static void LogError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                File.AppendAllText(Path.Combine(Settings.ConfigDir, "errors.log"), $"[{DateTime.Now:u}] {ex}\n\n");
            }
            catch { }
        }
    }
}
