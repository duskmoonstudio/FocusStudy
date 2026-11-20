using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FocusStudy
{
    /// <summary>User settings, stored in %AppData%\FocusStudy\settings.json (editable by hand).</summary>
    public class Settings
    {
        public string StudyFolder { get; set; } = "";
        public int SessionMinutes { get; set; } = 60;
        public int IdleMinutes { get; set; } = 5;
        public int ExitCooldownSeconds { get; set; } = 90;
        public string HomePage { get; set; } = "https://www.google.com";
        public string PythonPath { get; set; } = "";
        public string OctavePath { get; set; } = "";
        public string MatlabPath { get; set; } = "";
        /// <summary>"auto" (MATLAB if installed, else Octave), "matlab" or "octave".</summary>
        public string MatlabEngine { get; set; } = "auto";

        /// <summary>Sites that cannot be opened at all during a session.</summary>
        public List<string> BlockedDomains { get; set; } = new List<string>
        {
            "instagram.com", "facebook.com", "tiktok.com", "twitter.com", "x.com", "reddit.com",
            "snapchat.com", "pinterest.com", "netflix.com", "primevideo.com", "hotstar.com",
            "twitch.tv", "9gag.com", "discord.com", "web.whatsapp.com", "web.telegram.org",
            "poki.com", "crazygames.com", "miniclip.com", "roblox.com"
        };

        /// <summary>Sites that are never flagged as off-topic (AI assistants, reference, learning).</summary>
        public List<string> TrustedDomains { get; set; } = new List<string>
        {
            "chatgpt.com", "chat.openai.com", "claude.ai", "gemini.google.com", "perplexity.ai",
            "copilot.microsoft.com", "chat.deepseek.com", "notebooklm.google.com",
            "scholar.google.com", "wikipedia.org", "khanacademy.org", "coursera.org", "edx.org",
            "nptel.ac.in", "ocw.mit.edu", "mathworks.com", "python.org", "stackoverflow.com",
            "geeksforgeeks.org", "w3schools.com", "wolframalpha.com", "desmos.com", "arxiv.org",
            "github.com", "octave.org", "numpy.org", "matplotlib.org", "brilliant.org"
        };

        /// <summary>Words in a page title that suggest entertainment rather than study.</summary>
        public List<string> DistractionKeywords { get; set; } = new List<string>
        {
            "music video", "official video", "lyrics", "song", "songs", "trailer", "teaser",
            "highlights", "funny", "prank", "meme", "memes", "gameplay", "let's play", "vlog",
            "reaction", "shorts", "full movie", "web series", "episode", "live score", "celebrity",
            "gossip", "comedy", "stand-up", "asmr", "unboxing", "fortnite", "minecraft", "gta",
            "bgmi", "free fire", "valorant", "ipl", "anime", "movie review", "roast", "tiktok"
        };

        /// <summary>Words that suggest study content; they outweigh distraction words.</summary>
        public List<string> StudyKeywords { get; set; } = new List<string>
        {
            "lecture", "tutorial", "course", "lesson", "explained", "explanation", "how to",
            "introduction", "chapter", "problem", "solution", "solved", "exam", "notes", "theory",
            "derivation", "proof", "physics", "chemistry", "mathematics", "math", "maths",
            "calculus", "algebra", "biology", "engineering", "programming", "python", "matlab",
            "octave", "algorithm", "data structure", "nptel", "khan academy", "lab", "assignment",
            "homework", "revision", "jee", "neet", "gate", "upsc", "documentation", "paper"
        };

        public static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusStudy");

        public static string LocalDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusStudy");

        public static string FilePath => Path.Combine(ConfigDir, "settings.json");

        static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
            }
            catch
            {
                // corrupt settings file: fall back to defaults
            }
            return new Settings();
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
    }
}
