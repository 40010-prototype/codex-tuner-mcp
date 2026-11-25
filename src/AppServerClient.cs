using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexDesktopTuning
{
    public sealed class AppServerClient : IDisposable
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 32 * 1024 * 1024
        };

        private Process _process;
        private StreamWriter _stdin;
        private StreamReader _stdout;
        private int _nextId = 1;
        public string CodexExePath { get; private set; }
        public bool IsAlive { get { return _process != null && !_process.HasExited; } }

        public AppServerClient(string codexExePath)
        {
            if (string.IsNullOrEmpty(codexExePath) || !File.Exists(codexExePath))
            {
                throw new TuningException("codex_cli_not_found", "Could not locate codex.exe for official app-server RPC.");
            }
            CodexExePath = codexExePath;

            var psi = new ProcessStartInfo
            {
                FileName = codexExePath,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _process = Process.Start(psi);
            if (_process == null)
            {
                throw new TuningException("app_server_start_failed", "Failed to start codex app-server.");
            }
            // Drain stderr asynchronously so buffers never block
            _process.ErrorDataReceived += delegate { };
            _process.BeginErrorReadLine();

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _stdout = _process.StandardOutput;

            var initParams = new Dictionary<string, object>
            {
                { "clientInfo", new Dictionary<string, object> { { "name", "codex-desktop-tuning" }, { "version", "2.0.0" } } },
                { "capabilities", new Dictionary<string, object> { { "experimentalApi", true } } }
            };
            Request("initialize", initParams, 15000);
            Notify("initialized", new Dictionary<string, object>());
        }

        private void Notify(string method, object parameters)
        {
            var msg = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "method", method },
                { "params", parameters }
            };
            _stdin.WriteLine(Json.Serialize(msg));
        }

        public Dictionary<string, object> Request(string method, object parameters, int timeoutMs)
        {
            int id = _nextId++;
            var msg = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "method", method },
                { "params", parameters }
            };
            _stdin.WriteLine(Json.Serialize(msg));

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int remaining = (int)Math.Max(100, timeoutMs - sw.ElapsedMilliseconds);
                string line = ReadLineWithTimeout(remaining);
                if (line == null)
                {
                    if (_process.HasExited)
                    {
                        throw new TuningException("app_server_exited", "codex app-server exited before replying to " + method);
                    }
                    throw new TuningException("app_server_timeout", "Timed out waiting for app-server reply to " + method);
                }
                line = line.Trim();
                if (line.Length == 0) continue;

                Dictionary<string, object> obj;
                try
                {
                    obj = Json.DeserializeObject(line) as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }
                if (obj == null || !obj.ContainsKey("id")) continue;
                object rawId = obj["id"];
                if (rawId == null) continue;
                int replyId;
                if (!int.TryParse(rawId.ToString(), out replyId) || replyId != id) continue;

                if (obj.ContainsKey("error") && obj["error"] != null)
                {
                    var err = obj["error"] as Dictionary<string, object>;
                    string errMsg = err != null && err.ContainsKey("message") && err["message"] != null
                        ? err["message"].ToString()
                        : "unknown_rpc_error";
                    if (errMsg.IndexOf("already has an active writer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new TuningException("thread_locked_by_active_writer", errMsg);
                    }
                    throw new TuningException("app_server_rpc_error", method + ": " + errMsg);
                }

                if (obj.ContainsKey("result"))
                {
                    return obj["result"] as Dictionary<string, object> ?? new Dictionary<string, object>();
                }
                return new Dictionary<string, object>();
            }

            throw new TuningException("app_server_timeout", "Timed out waiting for app-server reply to " + method);
        }

        private string ReadLineWithTimeout(int timeoutMs)
        {
            var task = _stdout.ReadLineAsync();
            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }
            return null;
        }

        public ModelCatalogCache FetchModelCatalog(string cliVersion, int ttlSeconds, string codexHome)
        {
            var models = new List<ModelInfo>();
            string cursor = null;
            for (int page = 0; page < 5; page++)
            {
                var p = new Dictionary<string, object> { { "includeHidden", false } };
                if (cursor != null) p["cursor"] = cursor;
                var resp = Request("model/list", p, 15000);
                var data = resp.ContainsKey("data") ? resp["data"] as object[] : null;
                if (data != null)
                {
                    foreach (object item in data)
                    {
                        var dict = item as Dictionary<string, object>;
                        if (dict == null) continue;
                        string id = Str(dict, "id") ?? Str(dict, "model");
                        if (string.IsNullOrEmpty(id)) continue;

                        var efforts = new List<EffortOption>();
                        var rawEfforts = dict.ContainsKey("supportedReasoningEfforts")
                            ? dict["supportedReasoningEfforts"] as object[]
                            : null;
                        if (rawEfforts != null)
                        {
                            foreach (object eo in rawEfforts)
                            {
                                var ed = eo as Dictionary<string, object>;
                                if (ed == null) continue;
                                string re = Str(ed, "reasoningEffort");
                                if (!string.IsNullOrEmpty(re))
                                {
                                    efforts.Add(new EffortOption
                                    {
                                        ReasoningEffort = re.ToLowerInvariant(),
                                        Description = Str(ed, "description") ?? ""
                                    });
                                }
                            }
                        }

                        models.Add(new ModelInfo
                        {
                            Id = id,
                            DisplayName = Str(dict, "displayName") ?? id,
                            IsDefault = Bool(dict, "isDefault"),
                            Hidden = Bool(dict, "hidden"),
                            DefaultReasoningEffort = (Str(dict, "defaultReasoningEffort") ?? "medium").ToLowerInvariant(),
                            SupportedReasoningEfforts = efforts
                        });
                    }
                }
                cursor = Str(resp, "nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }

            string defaultModel = null;
            string defaultEffort = null;
            var enabledEfforts = new List<string>();
            try
            {
                var cfgResp = Request("config/read", new Dictionary<string, object>(), 10000);
                var cfg = cfgResp.ContainsKey("config") ? cfgResp["config"] as Dictionary<string, object> : null;
                if (cfg != null)
                {
                    defaultModel = Str(cfg, "model");
                    defaultEffort = Str(cfg, "model_reasoning_effort");
                    var desktop = cfg.ContainsKey("desktop") ? cfg["desktop"] as Dictionary<string, object> : null;
                    if (desktop != null && desktop.ContainsKey("enabled-reasoning-efforts"))
                    {
                        var arr = desktop["enabled-reasoning-efforts"] as object[];
                        if (arr != null)
                        {
                            foreach (object v in arr)
                            {
                                if (v != null) enabledEfforts.Add(v.ToString().ToLowerInvariant());
                            }
                        }
                    }
                }
            }
            catch
            {
                enabledEfforts = VersionDetector.ReadConfigEnabledEfforts(codexHome);
            }

            if (string.IsNullOrEmpty(defaultModel))
            {
                foreach (ModelInfo m in models)
                {
                    if (m.IsDefault) { defaultModel = m.Id; break; }
                }
            }

            return new ModelCatalogCache
            {
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
                TtlSeconds = ttlSeconds > 0 ? ttlSeconds : CacheStore.DefaultModelsTtlSeconds,
                CodexCliVersion = cliVersion,
                DefaultModel = defaultModel,
                DefaultEffort = defaultEffort,
                EnabledDesktopEfforts = enabledEfforts,
                Models = models
            };
        }

        public ThreadBackendState ReadThread(string threadId, string codexHome)
        {
            var resp = Request("thread/read", new Dictionary<string, object>
            {
                { "threadId", threadId },
                { "includeTurns", false }
            }, 15000);

            var thread = resp.ContainsKey("thread") ? resp["thread"] as Dictionary<string, object> : null;
            if (thread == null)
            {
                throw new TuningException("thread_not_found", "Thread not found in Codex app-server: " + threadId);
            }

            var statusDict = thread.ContainsKey("status") ? thread["status"] as Dictionary<string, object> : null;
            long updatedAt = 0;
            if (thread.ContainsKey("updatedAt") && thread["updatedAt"] != null)
            {
                long.TryParse(thread["updatedAt"].ToString(), out updatedAt);
            }

            return new ThreadBackendState
            {
                ThreadId = Str(thread, "id") ?? threadId,
                Name = Str(thread, "name"),
                Model = Str(thread, "model"),
                ReasoningEffort = Str(thread, "reasoningEffort"),
                Cwd = Str(thread, "cwd"),
                RolloutPath = Str(thread, "path"),
                CliVersion = Str(thread, "cliVersion"),
                StatusType = statusDict != null ? Str(statusDict, "type") : null,
                WriterLockHeld = IsThreadWriterLocked(codexHome, threadId),
                UpdatedAt = updatedAt,
                Source = "app_server_thread_read"
            };
        }

        public ThreadBackendState UpdateUnloadedThreadSettings(string threadId, string codexHome, string modelId, string effort)
        {
            if (IsThreadWriterLocked(codexHome, threadId))
            {
                throw new TuningException("thread_locked_by_active_writer",
                    "Thread is currently loaded by Codex Desktop (writer lock held); cannot mutate via short-lived app-server.");
            }

            bool resumed = false;
            try
            {
                Request("thread/resume", new Dictionary<string, object> { { "threadId", threadId } }, 15000);
                resumed = true;

                var updateParams = new Dictionary<string, object> { { "threadId", threadId } };
                if (!string.IsNullOrEmpty(modelId)) updateParams["model"] = modelId;
                if (!string.IsNullOrEmpty(effort)) updateParams["effort"] = effort;

                Request("thread/settings/update", updateParams, 15000);
                return ReadThread(threadId, codexHome);
            }
            finally
            {
                if (resumed)
                {
                    try { Request("thread/unsubscribe", new Dictionary<string, object> { { "threadId", threadId } }, 5000); }
                    catch { }
                }
            }
        }

        public ThreadBackendState CreateThread(string cwd, string codexHome, string modelId, string effort, string title)
        {
            // Start without pre-populating model/effort so subsequent thread/settings/update
            // is guaranteed to emit a persisted thread_settings_applied event into the rollout JSONL.
            var startParams = new Dictionary<string, object>
            {
                { "cwd", cwd },
                { "ephemeral", false }
            };

            var startResp = Request("thread/start", startParams, 15000);
            var threadObj = startResp.ContainsKey("thread") ? startResp["thread"] as Dictionary<string, object> : null;
            string threadId = threadObj != null ? Str(threadObj, "id") : null;
            if (string.IsNullOrEmpty(threadId))
            {
                throw new TuningException("thread_start_failed", "codex app-server thread/start did not return a thread id.");
            }

            try
            {
                if (!string.IsNullOrEmpty(title))
                {
                    Request("thread/name/set", new Dictionary<string, object>
                    {
                        { "threadId", threadId },
                        { "name", title }
                    }, 10000);
                }

                if (!string.IsNullOrEmpty(modelId) || !string.IsNullOrEmpty(effort))
                {
                    // Unsubscribe and resume once so Codex materializes the rollout file before applying settings
                    try { Request("thread/unsubscribe", new Dictionary<string, object> { { "threadId", threadId } }, 5000); } catch { }
                    Request("thread/resume", new Dictionary<string, object> { { "threadId", threadId } }, 15000);

                    var updateParams = new Dictionary<string, object> { { "threadId", threadId } };
                    if (!string.IsNullOrEmpty(modelId)) updateParams["model"] = modelId;
                    if (!string.IsNullOrEmpty(effort)) updateParams["effort"] = effort;
                    Request("thread/settings/update", updateParams, 15000);
                }

                return ReadThread(threadId, codexHome);
            }
            finally
            {
                try { Request("thread/unsubscribe", new Dictionary<string, object> { { "threadId", threadId } }, 5000); }
                catch { }
            }
        }

        public static bool IsThreadWriterLocked(string codexHome, string threadId)
        {
            string lockPath = Path.Combine(codexHome, "thread-writer-locks", threadId + ".lock");
            if (!File.Exists(lockPath)) return false;
            try
            {
                using (var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return null;
            return d[key].ToString();
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return false;
            bool b;
            return bool.TryParse(d[key].ToString(), out b) && b;
        }

        public void Dispose()
        {
            if (_process != null)
            {
                try
                {
                    if (_stdin != null) { _stdin.Close(); _stdin = null; }
                }
                catch { }
                try
                {
                    if (!_process.HasExited)
                    {
                        if (!_process.WaitForExit(1500))
                        {
                            _process.Kill();
                        }
                    }
                }
                catch { }
                try { _process.Dispose(); } catch { }
                _process = null;
            }
        }
    }
}
