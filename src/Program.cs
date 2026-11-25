using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexDesktopTuning
{
    public static class Program
    {
        private const string MutexName = @"Local\CodexDesktopTuning";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 16 * 1024 * 1024
        };

        private static bool _persistentAppServerEnabled = false;
        private static AppServerClient _sharedAppServer;

        public static void EnablePersistentAppServer()
        {
            _persistentAppServerEnabled = true;
        }

        public static void ShutdownPersistentAppServer()
        {
            if (_sharedAppServer != null)
            {
                try { _sharedAppServer.Dispose(); } catch { }
                _sharedAppServer = null;
            }
        }

        private static T WithAppServer<T>(string codexExePath, Func<AppServerClient, T> action)
        {
            if (!_persistentAppServerEnabled)
            {
                using (var client = new AppServerClient(codexExePath))
                {
                    return action(client);
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_sharedAppServer == null || !_sharedAppServer.IsAlive ||
                    !string.Equals(_sharedAppServer.CodexExePath, codexExePath, StringComparison.OrdinalIgnoreCase))
                {
                    ShutdownPersistentAppServer();
                    _sharedAppServer = new AppServerClient(codexExePath);
                }
                try
                {
                    return action(_sharedAppServer);
                }
                catch (TuningException ex)
                {
                    if (attempt == 0 && (ex.Code == "app_server_exited" || ex.Code == "app_server_timeout"))
                    {
                        ShutdownPersistentAppServer();
                        continue;
                    }
                    throw;
                }
                catch (System.IO.IOException)
                {
                    if (attempt == 0)
                    {
                        ShutdownPersistentAppServer();
                        continue;
                    }
                    throw;
                }
            }
            throw new TuningException("app_server_unavailable", "Failed to communicate with persistent codex app-server.");
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            string command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "help";

            if (command == "mcp" || command == "--mcp")
            {
                return McpServer.Run();
            }
            if (command == "help" || command == "-h" || command == "--help")
            {
                PrintHelp();
                return 0;
            }

            var opts = ParseArgs(args);
            bool pretty = opts.ContainsKey("pretty");
            int exitCode;
            Dictionary<string, object> payload = ExecuteCommand(command, opts, out exitCode);
            Emit(payload, pretty);
            return exitCode;
        }

        public static Dictionary<string, object> ExecuteCommand(
            string command,
            Dictionary<string, string> opts,
            out int exitCode)
        {
            exitCode = 0;
            bool mayHaveChanged = false;
            Mutex mutex = null;
            bool locked = false;

            try
            {
                string codexHome = VersionDetector.GetCodexHome();
                VersionFingerprint fp = VersionDetector.Detect();
                bool versionChanged;
                CompatCache compat = CacheStore.LoadCompatCache(fp, out versionChanged);

                int ttlSeconds = CacheStore.DefaultModelsTtlSeconds;
                if (opts.ContainsKey("ttl-hours"))
                {
                    int h;
                    if (int.TryParse(opts["ttl-hours"], out h) && h > 0) ttlSeconds = h * 3600;
                }

                if (command == "doctor")
                {
                    return RunDoctor(fp, compat, versionChanged, codexHome, ttlSeconds, opts, out exitCode);
                }

                if (command == "capabilities")
                {
                    return RunCapabilities(fp, compat, ttlSeconds);
                }

                if (command == "set-preference")
                {
                    string fm = RequireOpt(opts, "focus-mode");
                    UserPreferences saved = CacheStore.SavePreferences(fm);
                    return new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "command", "set-preference" },
                        { "preferencesPath", CacheStore.PreferencesPath() },
                        { "preferences", saved }
                    };
                }

                if (command == "create-thread")
                {
                    return RunCreateThread(fp, codexHome, ttlSeconds, opts);
                }

                if (versionChanged) CacheStore.SaveCompatCache(compat);

                if (command == "models")
                {
                    bool forceRefresh = opts.ContainsKey("refresh");
                    bool fromCache;
                    ModelCatalogCache catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, forceRefresh, out fromCache);
                    return new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "command", "models" },
                        { "fromCache", fromCache },
                        { "codexCliVersion", fp.CodexCliVersion },
                        { "updatedAtUtc", catalog.UpdatedAtUtc },
                        { "ttlSeconds", catalog.TtlSeconds },
                        { "defaultModel", catalog.DefaultModel },
                        { "defaultEffort", catalog.DefaultEffort },
                        { "enabledDesktopEfforts", catalog.EnabledDesktopEfforts },
                        { "models", catalog.Models }
                    };
                }

                Guid threadGuid = ParseThreadId(opts);
                string title = opts.ContainsKey("title") ? opts["title"] : null;

                if (command == "query")
                {
                    return RunQuery(threadGuid, title, fp, codexHome, ttlSeconds, opts);
                }

                if (command == "set-model" || command == "set-effort" || command == "set")
                {
                    int lockTimeoutMs = 0;
                    if (opts.ContainsKey("lock-timeout-ms")) int.TryParse(opts["lock-timeout-ms"], out lockTimeoutMs);

                    mutex = new Mutex(false, MutexName);
                    try
                    {
                        locked = mutex.WaitOne(lockTimeoutMs);
                    }
                    catch (AbandonedMutexException)
                    {
                        locked = true;
                    }
                    if (!locked)
                    {
                        throw new TuningException("another_tuning_operation_is_running",
                            "Another CodexDesktopTuning operation holds the concurrency mutex.");
                    }

                    return RunSet(command, threadGuid, title, fp, compat, codexHome, ttlSeconds, opts, ref mayHaveChanged);
                }

                throw new TuningException("unknown_command", "Unknown command: " + command);
            }
            catch (TuningException ex)
            {
                if (ex.MayHaveChanged) mayHaveChanged = true;
                exitCode = ex.Code == "compatibility_circuit_breaker_open" ? 2 : 1;
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "command", command },
                    { "error", ex.Code },
                    { "message", ex.Message },
                    { "isCompatError", ex.IsCompatError },
                    { "mayHaveChanged", mayHaveChanged }
                };
            }
            catch (Exception ex)
            {
                exitCode = 1;
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "command", command },
                    { "error", "unexpected_error" },
                    { "message", ex.Message },
                    { "mayHaveChanged", mayHaveChanged }
                };
            }
            finally
            {
                if (locked && mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                if (mutex != null)
                {
                    try { mutex.Dispose(); } catch { }
                }
            }
        }

        private static Dictionary<string, object> RunCapabilities(
            VersionFingerprint fp,
            CompatCache compat,
            int ttlSeconds)
        {
            DesktopWindowStatus desk = DesktopUiaController.InspectDesktop();
            bool modelsFresh;
            ModelCatalogCache cachedModels = CacheStore.LoadModelsCache(fp.CodexCliVersion, ttlSeconds, out modelsFresh);

            UserPreferences prefs = CacheStore.LoadPreferences();

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "command", "capabilities" },
                { "serverVersion", "2.5.2" },
                { "tools", new[] { "doctor", "capabilities", "models", "query", "set", "set_preference", "create_thread" } },
                { "genericCommandExecution", false },
                { "persistentAppServer", _persistentAppServerEnabled },
                { "preferencesPath", CacheStore.PreferencesPath() },
                { "preferences", prefs },
                { "defaults", new Dictionary<string, object>
                    {
                        { "querySource", "backend" },
                        { "allowForegroundFallback", true },
                        { "allowUnminimize", true },
                        { "focusMode", prefs.FocusMode }
                    }
                },
                { "desktop", desk },
                { "version", fp },
                { "paths", new Dictionary<string, object>
                    {
                        { "unlockedThreads", new Dictionary<string, object>
                            {
                                { "available", !string.IsNullOrEmpty(fp.CodexCliPath) },
                                { "mechanism", "official_codex_app_server_thread_settings_update" },
                                { "backgroundCapable", true },
                                { "inactiveSessionCapable", true }
                            }
                        },
                        { "desktopLoadedThreads", new Dictionary<string, object>
                            {
                                { "available", desk.IsInteractiveDefaultDesktop && desk.VisibleWindowCount == 1 && (compat.CircuitBreaker == null || !compat.CircuitBreaker.Tripped) },
                                { "mechanism", "windows_ui_automation" },
                                { "modelSwitchBackgroundCapable", desk.IsInteractiveDefaultDesktop },
                                { "effortSwitchRequiresForegroundFallback", true },
                                { "inactiveSessionRequiresOptInSwitch", true },
                                { "circuitBreakerTripped", compat.CircuitBreaker != null && compat.CircuitBreaker.Tripped }
                            }
                        }
                    }
                },
                { "safetyInvariants", new[]
                    {
                        "uuid_deep_link_verification",
                        "idle_and_empty_composer_verification",
                        "clipboard_sequence_guard_and_restore",
                        "post_write_readback_verification",
                        "cross_process_mutex",
                        "user_input_idle_backoff",
                        "version_fingerprint_cache_and_circuit_breaker",
                        "never_submits_prompts_or_triggers_inference"
                    }
                },
                { "modelsCache", new Dictionary<string, object>
                    {
                        { "present", cachedModels != null },
                        { "fresh", modelsFresh },
                        { "count", cachedModels != null && cachedModels.Models != null ? cachedModels.Models.Count : 0 }
                    }
                }
            };
        }

        private static Dictionary<string, object> RunDoctor(
            VersionFingerprint fp,
            CompatCache compat,
            bool versionChanged,
            string codexHome,
            int ttlSeconds,
            Dictionary<string, string> opts,
            out int exitCode)
        {
            bool resetBreaker = opts.ContainsKey("reset-breaker");
            if (resetBreaker)
            {
                CacheStore.ResetCircuitBreaker(compat);
            }

            DesktopWindowStatus desk = DesktopUiaController.InspectDesktop();
            bool modelsFresh;
            ModelCatalogCache cachedModels = CacheStore.LoadModelsCache(fp.CodexCliVersion, ttlSeconds, out modelsFresh);

            bool mutexBusy = false;
            using (var m = new Mutex(false, MutexName))
            {
                bool got = false;
                try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; }
                if (got) { try { m.ReleaseMutex(); } catch { } }
                else mutexBusy = true;
            }

            ThreadBackendState threadProbe = null;
            string threadProbeError = null;
            if (opts.ContainsKey("thread-id"))
            {
                try
                {
                    Guid g = ParseThreadId(opts);
                    threadProbe = WithAppServer(fp.CodexCliPath, c => c.ReadThread(g.ToString(), codexHome));
                }
                catch (Exception ex)
                {
                    threadProbeError = ex.Message;
                }
            }

            var warnings = new List<string>();
            if (!desk.IsInteractiveDefaultDesktop)
            {
                warnings.Add("Running on isolated desktop '" + desk.DesktopName + "'; UIA window control requires interactive WinSta0\\Default desktop.");
            }
            if (compat.CircuitBreaker != null && compat.CircuitBreaker.Tripped)
            {
                warnings.Add("Compatibility circuit breaker is OPEN for this Codex version; run with --reset-breaker after verifying.");
            }
            if (string.IsNullOrEmpty(fp.CodexCliPath))
            {
                warnings.Add("codex.exe not found; backend app-server queries and model discovery unavailable.");
            }

            bool tripped = compat.CircuitBreaker != null && compat.CircuitBreaker.Tripped;
            exitCode = tripped ? 2 : 0;

            var result = new Dictionary<string, object>
            {
                { "ok", !tripped },
                { "command", "doctor" },
                { "readOnly", !resetBreaker },
                { "version", fp },
                { "versionChangedSinceLastCheck", versionChanged },
                { "desktop", desk },
                { "mutexBusy", mutexBusy },
                { "compatCachePath", CacheStore.CompatCachePath() },
                { "preferencesPath", CacheStore.PreferencesPath() },
                { "preferences", CacheStore.LoadPreferences() },
                { "uiStrategy", compat.UiStrategy },
                { "circuitBreaker", compat.CircuitBreaker },
                { "modelsCache", new Dictionary<string, object>
                    {
                        { "path", CacheStore.ModelsCachePath() },
                        { "present", cachedModels != null },
                        { "fresh", modelsFresh },
                        { "updatedAtUtc", cachedModels != null ? cachedModels.UpdatedAtUtc : null },
                        { "count", cachedModels != null && cachedModels.Models != null ? cachedModels.Models.Count : 0 }
                    }
                },
                { "warnings", warnings }
            };
            if (threadProbe != null) result["threadProbe"] = threadProbe;
            if (threadProbeError != null) result["threadProbeError"] = threadProbeError;
            return result;
        }

        private static Dictionary<string, object> RunQuery(
            Guid threadGuid,
            string title,
            VersionFingerprint fp,
            string codexHome,
            int ttlSeconds,
            Dictionary<string, string> opts)
        {
            string source = opts.ContainsKey("source") ? opts["source"].ToLowerInvariant() : "backend";
            string threadId = threadGuid.ToString();

            bool fromCache;
            ModelCatalogCache catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, false, out fromCache);

            ThreadBackendState backend = null;
            if (source == "auto" || source == "backend")
            {
                backend = WithAppServer(fp.CodexCliPath, c => c.ReadThread(threadId, codexHome));
                if (string.IsNullOrEmpty(title) && backend != null) title = backend.Name;
            }

            UiThreadState ui = null;
            if (source == "auto" || source == "ui")
            {
                if (string.IsNullOrEmpty(title))
                {
                    if (source == "ui") throw new TuningException("invalid_arguments", "--title is required for --source ui");
                }
                else
                {
                    var uia = new DesktopUiaController();
                    ui = uia.QueryUi(threadGuid, title, catalog);
                    if (source == "ui" && !ui.Visible)
                    {
                        throw new TuningException(ui.Reason ?? "ui_query_unavailable", "UI query unavailable: " + ui.Reason);
                    }
                }
            }

            string effectiveModel = (ui != null && ui.Visible && !string.IsNullOrEmpty(ui.ModelId))
                ? ui.ModelId
                : (backend != null ? backend.Model : (ui != null ? ui.ModelLabel : null));
            string effectiveEffort = (ui != null && ui.Visible && !string.IsNullOrEmpty(ui.Effort))
                ? ui.Effort
                : (backend != null ? backend.ReasoningEffort : null);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "command", "query" },
                { "threadId", threadId },
                { "title", title },
                { "model", effectiveModel },
                { "effort", effectiveEffort },
                { "writerLockHeld", backend != null && backend.WriterLockHeld },
                { "backend", backend },
                { "ui", ui },
                { "changed", false }
            };
        }

        private static Dictionary<string, object> RunCreateThread(
            VersionFingerprint fp,
            string codexHome,
            int ttlSeconds,
            Dictionary<string, string> opts)
        {
            string rawCwd = RequireOpt(opts, "cwd");
            string fullCwd;
            try
            {
                fullCwd = System.IO.Path.GetFullPath(rawCwd);
            }
            catch (Exception ex)
            {
                throw new TuningException("invalid_arguments", "Invalid --cwd path '" + rawCwd + "': " + ex.Message);
            }
            if (!System.IO.Directory.Exists(fullCwd))
            {
                throw new TuningException("invalid_arguments", "Target --cwd directory does not exist: " + fullCwd);
            }

            string targetModelInput = opts.ContainsKey("model") ? opts["model"] : null;
            string targetEffortInput = opts.ContainsKey("effort") ? opts["effort"].ToLowerInvariant() : null;
            string title = opts.ContainsKey("title") ? opts["title"] : null;

            bool fromCache;
            ModelCatalogCache catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, false, out fromCache);

            ModelInfo resolvedTargetModel = null;
            if (!string.IsNullOrEmpty(targetModelInput))
            {
                resolvedTargetModel = CacheStore.FindModel(catalog, targetModelInput);
                if (resolvedTargetModel == null && fromCache)
                {
                    catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, true, out fromCache);
                    resolvedTargetModel = CacheStore.FindModel(catalog, targetModelInput);
                }
                if (resolvedTargetModel == null)
                {
                    throw new TuningException("model_not_found_in_catalog",
                        "Requested model '" + targetModelInput + "' was not found even after refreshing Codex model catalog.");
                }
            }

            string effectiveModelForEffort = resolvedTargetModel != null
                ? resolvedTargetModel.Id
                : catalog.DefaultModel;
            if (!string.IsNullOrEmpty(targetEffortInput))
            {
                ModelInfo modelForEffort = CacheStore.FindModel(catalog, effectiveModelForEffort);
                bool effortFound = modelForEffort != null && modelForEffort.SupportedReasoningEfforts != null &&
                    modelForEffort.SupportedReasoningEfforts.Exists(e =>
                        string.Equals(e.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase));
                if (!effortFound && fromCache)
                {
                    catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, true, out fromCache);
                    modelForEffort = CacheStore.FindModel(catalog, effectiveModelForEffort);
                    effortFound = modelForEffort != null && modelForEffort.SupportedReasoningEfforts != null &&
                        modelForEffort.SupportedReasoningEfforts.Exists(e =>
                            string.Equals(e.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase));
                }
                if (!effortFound)
                {
                    throw new TuningException("effort_not_supported_for_model",
                        "Effort '" + targetEffortInput + "' is not advertised as supported for model '" + effectiveModelForEffort + "'.");
                }
            }

            string modelIdToSet = resolvedTargetModel != null ? resolvedTargetModel.Id : null;
            ThreadBackendState created;
            // Use a short-lived app-server process for thread/start so OS releases thread-writer-locks immediately on exit
            using (var writerClient = new AppServerClient(fp.CodexCliPath))
            {
                created = writerClient.CreateThread(fullCwd, codexHome, modelIdToSet, targetEffortInput, title);
            }
            // Read back AFTER the writer process exits to prove settings are truly persisted on disk & lock is released
            created = WithAppServer(fp.CodexCliPath, c => c.ReadThread(created.ThreadId, codexHome));

            if ((!string.IsNullOrEmpty(modelIdToSet) && !string.Equals(created.Model, modelIdToSet, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(targetEffortInput) && !string.Equals(created.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase)))
            {
                throw new TuningException("settings_readback_mismatch",
                    "New thread readback did not match requested model/effort (got model=" + created.Model + ", effort=" + created.ReasoningEffort + ").", false, true);
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "command", "create-thread" },
                { "path", "official_app_server_thread_start" },
                { "threadId", created.ThreadId },
                { "title", created.Name },
                { "cwd", created.Cwd },
                { "model", created.Model },
                { "effort", created.ReasoningEffort },
                { "rolloutPath", created.RolloutPath },
                { "writerLockHeld", AppServerClient.IsThreadWriterLocked(codexHome, created.ThreadId) }
            };
        }

        private static Dictionary<string, object> RunSet(
            string command,
            Guid threadGuid,
            string title,
            VersionFingerprint fp,
            CompatCache compat,
            string codexHome,
            int ttlSeconds,
            Dictionary<string, string> opts,
            ref bool mayHaveChanged)
        {
            string threadId = threadGuid.ToString();
            string targetModelInput = null;
            string targetEffortInput = null;

            if (command == "set-model")
            {
                targetModelInput = RequireOpt(opts, "value");
            }
            else if (command == "set-effort")
            {
                targetEffortInput = RequireOpt(opts, "value").ToLowerInvariant();
            }
            else // "set"
            {
                if (opts.ContainsKey("model")) targetModelInput = opts["model"];
                if (opts.ContainsKey("effort")) targetEffortInput = opts["effort"].ToLowerInvariant();
                if (string.IsNullOrEmpty(targetModelInput) && string.IsNullOrEmpty(targetEffortInput))
                {
                    throw new TuningException("invalid_arguments", "set requires --model and/or --effort.");
                }
            }

            bool fromCache;
            ModelCatalogCache catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, false, out fromCache);
            ModelInfo resolvedTargetModel = null;
            if (!string.IsNullOrEmpty(targetModelInput))
            {
                resolvedTargetModel = CacheStore.FindModel(catalog, targetModelInput);
                if (resolvedTargetModel == null && fromCache)
                {
                    catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, true, out fromCache);
                    resolvedTargetModel = CacheStore.FindModel(catalog, targetModelInput);
                }
                if (resolvedTargetModel == null)
                {
                    throw new TuningException("model_not_found_in_catalog",
                        "Requested model '" + targetModelInput + "' was not found even after refreshing Codex model catalog.");
                }
            }

            ThreadBackendState beforeBackend = WithAppServer(fp.CodexCliPath, c => c.ReadThread(threadId, codexHome));
            if (string.IsNullOrEmpty(title)) title = beforeBackend.Name;

            string effectiveModelIdForValidation = resolvedTargetModel != null
                ? resolvedTargetModel.Id
                : beforeBackend.Model;
            if (!string.IsNullOrEmpty(targetEffortInput))
            {
                ModelInfo modelForEffort = CacheStore.FindModel(catalog, effectiveModelIdForValidation);
                bool effortFound = modelForEffort != null && modelForEffort.SupportedReasoningEfforts != null &&
                    modelForEffort.SupportedReasoningEfforts.Exists(e =>
                        string.Equals(e.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase));
                if (!effortFound && fromCache)
                {
                    catalog = GetOrRefreshModels(fp, codexHome, ttlSeconds, true, out fromCache);
                    modelForEffort = CacheStore.FindModel(catalog, effectiveModelIdForValidation);
                    effortFound = modelForEffort != null && modelForEffort.SupportedReasoningEfforts != null &&
                        modelForEffort.SupportedReasoningEfforts.Exists(e =>
                            string.Equals(e.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase));
                }
                if (!effortFound)
                {
                    throw new TuningException("effort_not_supported_for_model",
                        "Effort '" + targetEffortInput + "' is not advertised as supported for model '" + effectiveModelIdForValidation + "'.");
                }
            }

            string focusMode;
            if (opts.ContainsKey("focus-mode"))
            {
                focusMode = CacheStore.NormalizeFocusMode(opts["focus-mode"]);
                if (focusMode == null)
                {
                    throw new TuningException("invalid_arguments",
                        "Invalid --focus-mode '" + opts["focus-mode"] + "'. Allowed values: restore, stay, focus_codex.");
                }
            }
            else
            {
                focusMode = CacheStore.LoadPreferences().FocusMode;
            }

            // Path A: Unloaded thread -> official app-server thread/settings/update
            if (!beforeBackend.WriterLockHeld)
            {
                bool needsModel = resolvedTargetModel != null &&
                    !string.Equals(beforeBackend.Model, resolvedTargetModel.Id, StringComparison.OrdinalIgnoreCase);
                bool needsEffort = !string.IsNullOrEmpty(targetEffortInput) &&
                    !string.Equals(beforeBackend.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase);

                ThreadBackendState afterBackend = beforeBackend;
                if (needsModel || needsEffort)
                {
                    mayHaveChanged = true;
                    // Use a short-lived app-server process for thread/resume so writer lock is released immediately on exit
                    using (var writerClient = new AppServerClient(fp.CodexCliPath))
                    {
                        afterBackend = writerClient.UpdateUnloadedThreadSettings(
                            threadId, codexHome,
                            needsModel ? resolvedTargetModel.Id : null,
                            needsEffort ? targetEffortInput : null);
                    }
                    if ((needsModel && !string.Equals(afterBackend.Model, resolvedTargetModel.Id, StringComparison.OrdinalIgnoreCase)) ||
                        (needsEffort && !string.Equals(afterBackend.ReasoningEffort, targetEffortInput, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new TuningException("settings_readback_mismatch",
                            "app-server thread/read readback did not match requested settings.", false, true);
                    }
                }

                if (focusMode == "focus_codex")
                {
                    DesktopUiaController.TryFocusCodexWindow();
                }

                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "command", command },
                    { "path", "official_app_server_unloaded" },
                    { "threadId", threadId },
                    { "title", title },
                    { "state", "idle" },
                    { "model", afterBackend.Model },
                    { "effort", afterBackend.ReasoningEffort },
                    { "focusMode", focusMode },
                    { "changed", needsModel || needsEffort }
                };
            }

            // Path B: Desktop-loaded thread -> UIA with circuit breaker & safety checks
            if (compat.CircuitBreaker != null && compat.CircuitBreaker.Tripped)
            {
                throw new TuningException("compatibility_circuit_breaker_open",
                    "UI tuning paused due to repeated compatibility failures on Codex version " + compat.FingerprintKey +
                    " (last error: " + compat.CircuitBreaker.LastErrorCode + "). Run 'doctor --reset-breaker' after verifying.");
            }

            if (string.IsNullOrEmpty(title))
            {
                throw new TuningException("invalid_arguments", "Thread title is required for Desktop UI tuning.");
            }

            bool allowFg = true;
            if (opts.ContainsKey("no-foreground-fallback"))
            {
                allowFg = false;
            }
            else if (opts.ContainsKey("allow-foreground-fallback"))
            {
                bool parsedFg;
                if (bool.TryParse(opts["allow-foreground-fallback"], out parsedFg)) allowFg = parsedFg;
            }
            bool allowSwitch = opts.ContainsKey("allow-session-switch") &&
                !string.Equals(opts["allow-session-switch"], "false", StringComparison.OrdinalIgnoreCase);
            bool allowUnminimize = true;
            if (opts.ContainsKey("no-unminimize"))
            {
                allowUnminimize = false;
            }
            else if (opts.ContainsKey("allow-unminimize"))
            {
                bool parsedUnmin;
                if (bool.TryParse(opts["allow-unminimize"], out parsedUnmin)) allowUnminimize = parsedUnmin;
            }
            bool ignoreIdle = opts.ContainsKey("ignore-user-idle") &&
                !string.Equals(opts["ignore-user-idle"], "false", StringComparison.OrdinalIgnoreCase);
            int userIdleMs = 2000;
            if (opts.ContainsKey("user-idle-ms")) int.TryParse(opts["user-idle-ms"], out userIdleMs);

            var uia = new DesktopUiaController();
            try
            {
                System.Diagnostics.Stopwatch totalSw = System.Diagnostics.Stopwatch.StartNew();
                UiThreadState uiAfter = uia.ApplyChangesUi(
                    threadGuid, title,
                    resolvedTargetModel != null ? resolvedTargetModel.Id : null,
                    targetEffortInput,
                    catalog, compat, allowFg, allowSwitch, allowUnminimize, focusMode, ignoreIdle, userIdleMs);

                mayHaveChanged = uia.MayHaveChanged;

                // Wait briefly (up to 400ms) for Codex Desktop's async rollout JSONL flush so persistedModel/persistedEffort stay in sync
                System.Diagnostics.Stopwatch backSw = System.Diagnostics.Stopwatch.StartNew();
                ThreadBackendState afterBackend = WithAppServer(fp.CodexCliPath, c => c.ReadThread(threadId, codexHome));
                string expectedModelId = uiAfter.ModelId ?? uiAfter.ModelLabel;
                while (uia.MayHaveChanged && backSw.ElapsedMilliseconds < 400 &&
                       ((!string.IsNullOrEmpty(expectedModelId) && !string.Equals(afterBackend.Model, expectedModelId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(uiAfter.Effort) && !string.Equals(afterBackend.ReasoningEffort, uiAfter.Effort, StringComparison.OrdinalIgnoreCase))))
                {
                    Thread.Sleep(50);
                    afterBackend = WithAppServer(fp.CodexCliPath, c => c.ReadThread(threadId, codexHome));
                }
                long backendAfterMs = backSw.ElapsedMilliseconds;
                long totalMs = totalSw.ElapsedMilliseconds;

                var timings = uia.LastTimingsMs != null
                    ? new Dictionary<string, long>(uia.LastTimingsMs)
                    : new Dictionary<string, long>();
                timings["backendAfterMs"] = backendAfterMs;
                timings["totalUiAndReadbackMs"] = totalMs;

                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "command", command },
                    { "path", "desktop_uia" },
                    { "threadId", threadId },
                    { "title", title },
                    { "state", "idle" },
                    { "model", uiAfter.ModelId ?? uiAfter.ModelLabel },
                    { "modelLabel", uiAfter.ModelLabel },
                    { "effort", uiAfter.Effort },
                    { "effortControlMode", uiAfter.EffortControlMode },
                    { "persistedModel", afterBackend.Model },
                    { "persistedEffort", afterBackend.ReasoningEffort },
                    { "focusMode", focusMode },
                    { "changed", uia.MayHaveChanged },
                    { "timingsMs", timings }
                };
            }
            catch (TuningException ex)
            {
                if (uia.MayHaveChanged) mayHaveChanged = true;
                if (ex.IsCompatError)
                {
                    CacheStore.RecordCompatFailure(compat, ex.Code, ex.Message);
                }
                throw;
            }
        }

        private static ModelCatalogCache GetOrRefreshModels(
            VersionFingerprint fp, string codexHome, int ttlSeconds, bool forceRefresh, out bool fromCache)
        {
            bool fresh;
            if (!forceRefresh)
            {
                ModelCatalogCache cached = CacheStore.LoadModelsCache(fp.CodexCliVersion, ttlSeconds, out fresh);
                if (cached != null && fresh)
                {
                    // Always refresh enabled-reasoning-efforts live from local config.toml (<1ms)
                    // so toggling 'max' or other efforts never goes stale within the 24h model cache TTL.
                    cached.EnabledDesktopEfforts = VersionDetector.ReadConfigEnabledEfforts(codexHome);
                    fromCache = true;
                    return cached;
                }
            }

            ModelCatalogCache live = WithAppServer(fp.CodexCliPath, c => c.FetchModelCatalog(fp.CodexCliVersion, ttlSeconds, codexHome));
            CacheStore.SaveModelsCache(live);
            fromCache = false;
            return live;
        }

        private static Guid ParseThreadId(Dictionary<string, string> opts)
        {
            string raw = RequireOpt(opts, "thread-id");
            Guid g;
            if (!Guid.TryParse(raw, out g) || g == Guid.Empty)
            {
                throw new TuningException("invalid_arguments", "Invalid UUID for --thread-id: " + raw);
            }
            return g;
        }

        private static string RequireOpt(Dictionary<string, string> opts, string key)
        {
            string val;
            if (!opts.TryGetValue(key, out val) || string.IsNullOrEmpty(val))
            {
                throw new TuningException("invalid_arguments", "Missing required option --" + key);
            }
            return val;
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) continue;
                string key = a.Substring(2);
                int eq = key.IndexOf('=');
                if (eq > 0)
                {
                    dict[key.Substring(0, eq)] = key.Substring(eq + 1);
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    dict[key] = args[++i];
                }
                else
                {
                    dict[key] = "true";
                }
            }
            return dict;
        }

        private static void Emit(object payload, bool pretty)
        {
            string raw = Json.Serialize(payload);
            Console.WriteLine(pretty ? FormatJson(raw) : raw);
        }

        private static string FormatJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (escape) { sb.Append(ch); escape = false; continue; }
                if (ch == '\\' && inString) { sb.Append(ch); escape = true; continue; }
                if (ch == '"') { inString = !inString; sb.Append(ch); continue; }
                if (inString) { sb.Append(ch); continue; }
                if (ch == '{' || ch == '[')
                {
                    sb.Append(ch);
                    sb.AppendLine();
                    indent++;
                    sb.Append(new string(' ', indent * 2));
                }
                else if (ch == '}' || ch == ']')
                {
                    sb.AppendLine();
                    indent = Math.Max(0, indent - 1);
                    sb.Append(new string(' ', indent * 2));
                    sb.Append(ch);
                }
                else if (ch == ',')
                {
                    sb.Append(ch);
                    sb.AppendLine();
                    sb.Append(new string(' ', indent * 2));
                }
                else if (ch == ':')
                {
                    sb.Append(": ");
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("CodexDesktopTuning - Local Codex model & reasoning effort controller");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  --mcp       Run as dedicated stdio MCP server (doctor, capabilities, models, query, set, set_preference, create_thread)");
            Console.WriteLine("  doctor      [--thread-id <uuid>] [--reset-breaker] [--pretty]");
            Console.WriteLine("  capabilities [--pretty]");
            Console.WriteLine("  models      [--refresh] [--ttl-hours <n>] [--pretty]");
            Console.WriteLine("  query       --thread-id <uuid> [--title <title>] [--source backend|auto|ui] [--pretty]");
            Console.WriteLine("  create-thread --cwd <dir> [--model <model>] [--effort <effort>] [--title <title>] [--pretty]");
            Console.WriteLine("  set-preference --focus-mode restore|stay|focus_codex [--pretty]");
            Console.WriteLine("  set-model   --thread-id <uuid> --value <model> [--title <title>] [--allow-session-switch] [--pretty]");
            Console.WriteLine("  set-effort  --thread-id <uuid> --value <effort> [--title <title>] [--allow-foreground-fallback] [--allow-session-switch] [--pretty]");
            Console.WriteLine("  set         --thread-id <uuid> [--model <model>] [--effort <effort>] [--title <title>] [--allow-foreground-fallback] [--allow-session-switch] [--pretty]");
        }
    }
}
