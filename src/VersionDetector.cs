using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodexDesktopTuning
{
    public static class VersionDetector
    {
        private static readonly string[] RuntimeFiles = new[]
        {
            "codex.exe",
            "codex-code-mode-host.exe",
            "codex-windows-sandbox-setup.exe",
            "codex-command-runner.exe"
        };

        public static string GetCodexHome()
        {
            string env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            {
                return env;
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".codex");
        }

        public static VersionFingerprint Detect()
        {
            var fp = new VersionFingerprint();

            // 1. Running ChatGPT.exe (Codex Desktop)
            Process[] procs = Process.GetProcessesByName("ChatGPT");
            foreach (Process p in procs)
            {
                try
                {
                    string exePath = p.MainModule != null ? p.MainModule.FileName : null;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        fp.RunningExePath = exePath;
                        FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exePath);
                        fp.RunningProductVersion = vi.ProductVersion;
                        fp.RunningFileVersion = vi.FileVersion;
                        break;
                    }
                }
                catch
                {
                    // Ignore inaccessible subprocesses
                }
                finally
                {
                    p.Dispose();
                }
            }

            // 2. Installed package path & AppxManifest.xml
            string pkgRoot = FindInstalledPackageRoot(fp.RunningExePath);
            if (!string.IsNullOrEmpty(pkgRoot))
            {
                fp.InstalledPackagePath = pkgRoot;
                string manifest = Path.Combine(pkgRoot, "AppxManifest.xml");
                if (File.Exists(manifest))
                {
                    fp.InstalledPackageVersion = ReadAppxVersion(manifest);
                }
                string owlIni = Path.Combine(pkgRoot, "app", "resources", "owl-app.ini");
                if (File.Exists(owlIni))
                {
                    fp.OwlAppVersion = ReadOwlAppVersion(owlIni);
                }
            }

            // 3. Codex CLI binary & version
            fp.CodexCliPath = FindCodexExe(pkgRoot);
            if (!string.IsNullOrEmpty(fp.CodexCliPath))
            {
                fp.CodexCliVersion = ReadCodexCliVersion(fp.CodexCliPath);
            }

            // 4. UI locale from config.toml
            fp.UiLocale = ReadConfigLocale(GetCodexHome()) ?? "zh-CN";
            return fp;
        }

        private static string FindInstalledPackageRoot(string runningExePath)
        {
            if (!string.IsNullOrEmpty(runningExePath))
            {
                // Usually <PackageRoot>\app\ChatGPT.exe
                string appDir = Path.GetDirectoryName(runningExePath);
                if (!string.IsNullOrEmpty(appDir))
                {
                    string parent = Path.GetDirectoryName(appDir);
                    if (!string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "AppxManifest.xml")))
                    {
                        return parent;
                    }
                }
            }

            // Fallback: check registry AppModel packages for OpenAI.Codex
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (key != null)
                    {
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            if (sub.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                            {
                                using (var pkgKey = key.OpenSubKey(sub))
                                {
                                    if (pkgKey != null)
                                    {
                                        string root = pkgKey.GetValue("PackageRootFolder") as string;
                                        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                                        {
                                            return root;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore registry errors
            }

            return null;
        }

        private static string ReadAppxVersion(string manifestPath)
        {
            try
            {
                XDocument doc = XDocument.Load(manifestPath);
                XElement identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
                if (identity != null)
                {
                    XAttribute ver = identity.Attribute("Version");
                    if (ver != null) return ver.Value;
                }
            }
            catch { }
            return null;
        }

        private static string ReadOwlAppVersion(string owlIniPath)
        {
            try
            {
                foreach (string line in File.ReadAllLines(owlIniPath, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("AppVersion=", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring("AppVersion=".Length).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        public static string FindCodexExe(string pkgRoot)
        {
            // 1. Check running codex.exe process
            Process[] procs = Process.GetProcessesByName("codex");
            foreach (Process p in procs)
            {
                try
                {
                    string exePath = p.MainModule != null ? p.MainModule.FileName : null;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        return exePath;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            // 2. Check %LOCALAPPDATA%\OpenAI\Codex\bin\<16-hex>\codex.exe (newest complete runtime)
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string binRoot = Path.Combine(localApp, "OpenAI", "Codex", "bin");
            if (Directory.Exists(binRoot))
            {
                var candidates = new List<FileInfo>();
                foreach (string dir in Directory.GetDirectories(binRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name != null && Regex.IsMatch(name, "^[0-9a-fA-F]{16}$"))
                    {
                        bool complete = true;
                        foreach (string rf in RuntimeFiles)
                        {
                            string p = Path.Combine(dir, rf);
                            if (!File.Exists(p)) { complete = false; break; }
                        }
                        if (complete)
                        {
                            candidates.Add(new FileInfo(Path.Combine(dir, "codex.exe")));
                        }
                    }
                }
                if (candidates.Count > 0)
                {
                    candidates.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    return candidates[0].FullName;
                }
            }

            // 3. Bundled in WindowsApps package
            if (!string.IsNullOrEmpty(pkgRoot))
            {
                string bundled = Path.Combine(pkgRoot, "app", "resources", "codex.exe");
                if (File.Exists(bundled)) return bundled;
            }

            return null;
        }

        private static string _cachedCliPath;
        private static DateTime _cachedCliWriteUtc;
        private static string _cachedCliVersion;

        private static string ReadCodexCliVersion(string codexExe)
        {
            try
            {
                DateTime writeUtc = File.GetLastWriteTimeUtc(codexExe);
                if (string.Equals(_cachedCliPath, codexExe, StringComparison.OrdinalIgnoreCase) &&
                    _cachedCliWriteUtc == writeUtc &&
                    !string.IsNullOrEmpty(_cachedCliVersion))
                {
                    return _cachedCliVersion;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = codexExe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    string ver = (output ?? "").Trim();
                    if (!string.IsNullOrEmpty(ver))
                    {
                        _cachedCliPath = codexExe;
                        _cachedCliWriteUtc = writeUtc;
                        _cachedCliVersion = ver;
                    }
                    return ver;
                }
            }
            catch
            {
                return null;
            }
        }

        public static string ReadConfigLocale(string codexHome)
        {
            string cfg = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(cfg)) return null;
            try
            {
                string text = File.ReadAllText(cfg, Encoding.UTF8);
                Match m = Regex.Match(text, @"localeOverride\s*=\s*""([^""]+)""");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
            return null;
        }

        public static List<string> ReadConfigEnabledEfforts(string codexHome)
        {
            var result = new List<string>();
            string cfg = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(cfg)) return result;
            try
            {
                string text = File.ReadAllText(cfg, Encoding.UTF8);
                Match m = Regex.Match(text, @"enabled-reasoning-efforts\s*=\s*\[([^\]]+)\]");
                if (m.Success)
                {
                    foreach (Match item in Regex.Matches(m.Groups[1].Value, @"""([^""]+)"""))
                    {
                        string val = item.Groups[1].Value.Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(val) && !result.Contains(val))
                        {
                            result.Add(val);
                        }
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
