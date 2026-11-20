using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FocusStudy
{
    public sealed class Verdict
    {
        public bool Distracting;
        public string Reason;
    }

    /// <summary>Tracks focus statistics for a session and classifies web pages as study / off-topic.</summary>
    public sealed class FocusMonitor
    {
        readonly Settings _s;
        readonly List<(Regex rx, string word)> _distract;
        readonly List<Regex> _study;
        readonly List<TimeSpan> _events = new List<TimeSpan>();

        public int IdleEvents, Warnings, Overrides, BlockedAttempts, ExitAttempts, ExitAbandoned;
        public TimeSpan IdleTime;
        public string ExitReason = "";

        public FocusMonitor(Settings s)
        {
            _s = s;
            _distract = s.DistractionKeywords.Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => (WordRx(k), k.Trim())).ToList();
            _study = s.StudyKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(WordRx).ToList();
        }

        static Regex WordRx(string k) =>
            new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(k.Trim().ToLowerInvariant()) + @"(?![\p{L}\p{N}])",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Records a focus-breaking event (idle, warning, blocked site) at a session offset.</summary>
        public void Mark(TimeSpan at) => _events.Add(at);

        public int RecentEvents(TimeSpan now, TimeSpan window) => _events.Count(t => now - t <= window);

        public static string NormalizeDomain(string d)
        {
            d = (d ?? "").Trim().ToLowerInvariant();
            if (d.StartsWith("http://")) d = d.Substring(7);
            if (d.StartsWith("https://")) d = d.Substring(8);
            if (d.StartsWith("www.")) d = d.Substring(4);
            int slash = d.IndexOf('/');
            if (slash >= 0) d = d.Substring(0, slash);
            return d;
        }

        public static bool HostMatches(string host, string domain)
        {
            domain = NormalizeDomain(domain);
            if (domain.Length == 0 || string.IsNullOrEmpty(host)) return false;
            host = host.ToLowerInvariant();
            return host == domain || host.EndsWith("." + domain);
        }

        public string BlockedMatch(string host) => _s.BlockedDomains.FirstOrDefault(d => HostMatches(host, d));

        public Verdict Classify(string url, string title)
        {
            var ok = new Verdict();
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return ok;
            if (u.Scheme != "http" && u.Scheme != "https") return ok;
            string host = u.Host.ToLowerInvariant();
            if (_s.TrustedDomains.Any(d => HostMatches(host, d))) return ok;

            if (HostMatches(host, "youtube.com"))
            {
                string path = u.AbsolutePath.ToLowerInvariant();
                if (path.StartsWith("/shorts"))
                    return new Verdict { Distracting = true, Reason = "YouTube Shorts are built for endless scrolling." };
                if (path == "/" || path.Length == 0)
                    return new Verdict { Distracting = true, Reason = "The YouTube home feed is designed to pull you in. Search for the exact topic you need instead." };
            }

            string t = (title ?? "").ToLowerInvariant();
            if (t.Length == 0) return ok;
            var hits = _distract.Where(d => d.rx.IsMatch(t)).Select(d => d.word).ToList();
            if (hits.Count == 0) return ok;
            int study = _study.Count(rx => rx.IsMatch(t));
            if (hits.Count > study)
                return new Verdict
                {
                    Distracting = true,
                    Reason = $"The page title mentions “{hits[0]}”, which doesn't look like study material."
                };
            return ok;
        }

        static string Dur(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return $"{Math.Max(0, (int)t.TotalSeconds)} s";
            int h = (int)t.TotalHours, m = t.Minutes;
            return h > 0 ? $"{h} h {m} min" : $"{m} min";
        }

        public int Score(TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds < 1) return 100;
            double active = Math.Max(0, (elapsed - IdleTime).TotalSeconds) / elapsed.TotalSeconds * 100;
            active -= 3 * Math.Max(0, Warnings - Overrides) + 1 * Overrides + 2 * BlockedAttempts;
            return (int)Math.Round(Math.Clamp(active, 0, 100));
        }

        public string Summary(TimeSpan planned, TimeSpan elapsed, bool completed)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Planned session:   {Dur(planned)}");
            sb.AppendLine($"Time in session:   {Dur(elapsed)}");
            sb.AppendLine($"Away / idle:       {Dur(IdleTime)}  ({IdleEvents}×)");
            sb.AppendLine($"Off-topic pages:   {Warnings}  ({Overrides} marked as study)");
            sb.AppendLine($"Blocked sites:     {BlockedAttempts} attempt(s)");
            sb.AppendLine($"Exit attempts:     {ExitAttempts}  ({ExitAbandoned} changed mind)");
            sb.AppendLine();
            sb.Append($"Focus score:       {Score(elapsed)} / 100");
            if (!completed && !string.IsNullOrWhiteSpace(ExitReason))
                sb.Append($"\n\nYou stopped because: “{ExitReason.Trim()}”");
            return sb.ToString();
        }

        public void AppendLog(TimeSpan planned, TimeSpan elapsed, bool completed)
        {
            try
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                string path = Path.Combine(Settings.ConfigDir, "sessions.csv");
                bool header = !File.Exists(path);
                using var w = new StreamWriter(path, append: true, Encoding.UTF8);
                if (header)
                    w.WriteLine("date,planned_min,actual_min,completed,idle_min,offtopic_warnings,overrides,blocked,exit_attempts,score,exit_reason");
                string reason = (ExitReason ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
                w.WriteLine(string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Math.Round(planned.TotalMinutes, 1),
                    Math.Round(elapsed.TotalMinutes, 1),
                    completed ? "yes" : "no",
                    Math.Round(IdleTime.TotalMinutes, 1),
                    Warnings, Overrides, BlockedAttempts, ExitAttempts, Score(elapsed),
                    "\"" + reason + "\""));
            }
            catch
            {
                // logging must never break the session
            }
        }
    }
}
