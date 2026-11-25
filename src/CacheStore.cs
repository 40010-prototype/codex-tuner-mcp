using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexDesktopTuning
{
    public static class CacheStore
    {
        public const int DefaultModelsTtlSeconds = 86400; // 24 hours
        public const int CircuitBreakerThreshold = 2;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 16 * 1024 * 1024
        };

        public static string GetCacheDir()
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localApp, "CodexDesktopTuning");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static string CompatCachePath()
        {
            return Path.Combine(GetCacheDir(), "compat-cache.json");
        }

        public static string ModelsCachePath()
        {
            return Path.Combine(GetCacheDir(), "models-cache.json");
        }

        public static string PreferencesPath()
        {
            return Path.Combine(GetCacheDir(), "preferences.json");
        }

        public static string NormalizeFocusMode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim().ToLowerInvariant();
            if (s == "restore" || s == "stay" || s == "focus_codex") return s;
            return null;
        }

        public static UserPreferences LoadPreferences()
        {
            UserPreferences prefs = ReadJson<UserPreferences>(PreferencesPath());
            string mode = prefs != null ? NormalizeFocusMode(prefs.FocusMode) : null;
            return new UserPreferences
            {
                FocusMode = mode ?? "restore",
                UpdatedAtUtc = prefs != null ? prefs.UpdatedAtUtc : null
            };
        }

        public static UserPreferences SavePreferences(string focusMode)
        {
            string normalized = NormalizeFocusMode(focusMode);
            if (normalized == null)
            {
                throw new TuningException("invalid_arguments",
                    "Invalid focusMode '" + focusMode + "'. Allowed values: restore, stay, focus_codex.");
            }
            var prefs = new UserPreferences
            {
                FocusMode = normalized,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o")
            };
            WriteJsonAtomic(PreferencesPath(), prefs);
            return prefs;
        }

        public static CompatCache LoadCompatCache(VersionFingerprint current, out bool versionChanged)
        {
            versionChanged = false;
            string currentKey = current.Key();
            CompatCache cache = ReadJson<CompatCache>(CompatCachePath());

            if (cache == null || string.IsNullOrEmpty(cache.FingerprintKey))
            {
                versionChanged = true;
                return NewCompatCache(current);
            }

            if (!string.Equals(cache.FingerprintKey, currentKey, StringComparison.Ordinal))
            {
                versionChanged = true;
                // Version changed: invalidate old UI strategy and reset breaker for new version
                return NewCompatCache(current);
            }

            if (cache.UiStrategy == null) cache.UiStrategy = NewStrategy();
            if (cache.CircuitBreaker == null) cache.CircuitBreaker = new CircuitBreakerState();
            cache.Fingerprint = current;
            cache.LastCheckedAtUtc = DateTime.UtcNow.ToString("o");
            return cache;
        }

        public static void SaveCompatCache(CompatCache cache)
        {
            cache.LastCheckedAtUtc = DateTime.UtcNow.ToString("o");
            WriteJsonAtomic(CompatCachePath(), cache);
        }

        public static void RecordCompatSuccess(CompatCache cache, string effortMode, int scaleTotal, string modelId, string effort)
        {
            if (cache.UiStrategy == null) cache.UiStrategy = NewStrategy();
            if (cache.CircuitBreaker == null) cache.CircuitBreaker = new CircuitBreakerState();

            cache.UiStrategy.PickerVerifiedAtUtc = DateTime.UtcNow.ToString("o");
            if (!string.IsNullOrEmpty(effortMode)) cache.UiStrategy.EffortControlMode = effortMode;
            if (scaleTotal > 0) cache.UiStrategy.VerifiedEffortScaleTotal = scaleTotal;
            if (!string.IsNullOrEmpty(modelId))
            {
                if (cache.UiStrategy.VerifiedModels == null) cache.UiStrategy.VerifiedModels = new List<string>();
                if (!cache.UiStrategy.VerifiedModels.Contains(modelId)) cache.UiStrategy.VerifiedModels.Add(modelId);
            }
            if (!string.IsNullOrEmpty(effort))
            {
                if (cache.UiStrategy.VerifiedEfforts == null) cache.UiStrategy.VerifiedEfforts = new List<string>();
                if (!cache.UiStrategy.VerifiedEfforts.Contains(effort)) cache.UiStrategy.VerifiedEfforts.Add(effort);
            }

            cache.CircuitBreaker.ConsecutiveFailures = 0;
            cache.CircuitBreaker.Tripped = false;
            cache.CircuitBreaker.LastErrorCode = null;
            cache.CircuitBreaker.LastErrorMessage = null;
            SaveCompatCache(cache);
        }

        public static void RecordCompatFailure(CompatCache cache, string errorCode, string errorMessage)
        {
            if (cache.UiStrategy == null) cache.UiStrategy = NewStrategy();
            if (cache.CircuitBreaker == null) cache.CircuitBreaker = new CircuitBreakerState();

            // Invalidate cached UI strategy on compatibility failure
            cache.UiStrategy.EffortControlMode = "unknown";
            cache.UiStrategy.PickerVerifiedAtUtc = null;

            cache.CircuitBreaker.ConsecutiveFailures += 1;
            cache.CircuitBreaker.LastErrorCode = errorCode;
            cache.CircuitBreaker.LastErrorMessage = errorMessage;

            if (cache.CircuitBreaker.ConsecutiveFailures >= CircuitBreakerThreshold)
            {
                cache.CircuitBreaker.Tripped = true;
                cache.CircuitBreaker.TrippedAtUtc = DateTime.UtcNow.ToString("o");
                cache.CircuitBreaker.TrippedFingerprintKey = cache.FingerprintKey;
            }
            SaveCompatCache(cache);
        }

        public static void ResetCircuitBreaker(CompatCache cache)
        {
            cache.CircuitBreaker = new CircuitBreakerState();
            cache.UiStrategy = NewStrategy();
            SaveCompatCache(cache);
        }

        public static ModelCatalogCache LoadModelsCache(string currentCliVersion, int ttlSeconds, out bool isFresh)
        {
            isFresh = false;
            ModelCatalogCache cache = ReadJson<ModelCatalogCache>(ModelsCachePath());
            if (cache == null || cache.Models == null || cache.Models.Count == 0)
            {
                return null;
            }
            if (!string.Equals(cache.CodexCliVersion ?? "", currentCliVersion ?? "", StringComparison.Ordinal))
            {
                return null;
            }
            DateTime updated;
            if (!DateTime.TryParse(cache.UpdatedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out updated))
            {
                return null;
            }
            int effectiveTtl = ttlSeconds > 0 ? ttlSeconds : (cache.TtlSeconds > 0 ? cache.TtlSeconds : DefaultModelsTtlSeconds);
            if ((DateTime.UtcNow - updated.ToUniversalTime()).TotalSeconds <= effectiveTtl)
            {
                isFresh = true;
            }
            return cache;
        }

        public static void SaveModelsCache(ModelCatalogCache catalog)
        {
            catalog.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            if (catalog.TtlSeconds <= 0) catalog.TtlSeconds = DefaultModelsTtlSeconds;
            WriteJsonAtomic(ModelsCachePath(), catalog);
        }

        public static ModelInfo FindModel(ModelCatalogCache catalog, string query)
        {
            if (catalog == null || catalog.Models == null || string.IsNullOrEmpty(query)) return null;
            string key = NormalizeModelKey(query);
            foreach (ModelInfo m in catalog.Models)
            {
                if (NormalizeModelKey(m.Id) == key || NormalizeModelKey(m.DisplayName) == key)
                {
                    return m;
                }
            }
            return null;
        }

        public static string NormalizeModelKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.ToLowerInvariant())
            {
                if (!char.IsWhiteSpace(c) && c != '-' && c != '_') sb.Append(c);
            }
            return sb.ToString();
        }

        private static CompatCache NewCompatCache(VersionFingerprint fp)
        {
            return new CompatCache
            {
                Fingerprint = fp,
                FingerprintKey = fp.Key(),
                LastCheckedAtUtc = DateTime.UtcNow.ToString("o"),
                UiStrategy = NewStrategy(),
                CircuitBreaker = new CircuitBreakerState()
            };
        }

        private static UiStrategyCache NewStrategy()
        {
            return new UiStrategyCache
            {
                StrategyVersion = "1",
                EffortControlMode = "unknown",
                VerifiedModels = new List<string>(),
                VerifiedEfforts = new List<string>()
            };
        }

        private static T ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                return Json.Deserialize<T>(raw);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteJsonAtomic(string path, object obj)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string text = Json.Serialize(obj);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
