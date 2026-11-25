using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexDesktopTuning
{
    /// <summary>
    /// Minimal, auditable, zero-dependency MCP stdio server (JSON-RPC 2.0 over newline-delimited stdin/stdout).
    /// Runs synchronously on the main [STAThread] so UIAutomation and Clipboard calls execute on STA.
    /// Exposes strictly 7 dedicated Codex tuning tools — zero generic shell/command execution.
    /// </summary>
    public static class McpServer
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 16 * 1024 * 1024
        };

        public static int Run()
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            Program.EnablePersistentAppServer();

            try
            {
                using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                using (var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;

                        Dictionary<string, object> req;
                        try
                        {
                            req = Json.DeserializeObject(line) as Dictionary<string, object>;
                        }
                        catch (Exception ex)
                        {
                            WriteError(writer, null, -32700, "Parse error: " + ex.Message);
                            continue;
                        }
                        if (req == null) continue;

                        bool hasId = req.ContainsKey("id");
                        object id = hasId ? req["id"] : null;
                        string method = req.ContainsKey("method") && req["method"] != null ? req["method"].ToString() : "";
                        var parameters = req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null;

                        // Notifications have no id and must not receive a response.
                        if (!hasId)
                        {
                            continue;
                        }

                        try
                        {
                            if (method == "initialize")
                            {
                                string clientProto = parameters != null && parameters.ContainsKey("protocolVersion") && parameters["protocolVersion"] != null
                                    ? parameters["protocolVersion"].ToString()
                                    : "2024-11-05";
                                WriteResult(writer, id, new Dictionary<string, object>
                                {
                                    { "protocolVersion", clientProto },
                                    { "capabilities", new Dictionary<string, object>
                                        {
                                            { "tools", new Dictionary<string, object> { { "listChanged", false } } }
                                        }
                                    },
                                    { "serverInfo", new Dictionary<string, object>
                                        {
                                            { "name", "codex-desktop-tuning" },
                                            { "version", "2.5.2" }
                                        }
                                    }
                                });
                            }
                            else if (method == "ping")
                            {
                                WriteResult(writer, id, new Dictionary<string, object>());
                            }
                            else if (method == "tools/list")
                            {
                                WriteResult(writer, id, new Dictionary<string, object>
                                {
                                    { "tools", BuildToolDefinitions() }
                                });
                            }
                            else if (method == "tools/call")
                            {
                                HandleToolCall(writer, id, parameters);
                            }
                            else
                            {
                                WriteError(writer, id, -32601, "Method not found: " + method);
                            }
                        }
                        catch (Exception ex)
                        {
                            WriteError(writer, id, -32603, "Internal error: " + ex.Message);
                        }
                    }
                }
            }
            finally
            {
                Program.ShutdownPersistentAppServer();
            }
            return 0;
        }

        private static void HandleToolCall(StreamWriter writer, object id, Dictionary<string, object> parameters)
        {
            string toolName = parameters != null && parameters.ContainsKey("name") && parameters["name"] != null
                ? parameters["name"].ToString()
                : "";
            var args = parameters != null && parameters.ContainsKey("arguments")
                ? parameters["arguments"] as Dictionary<string, object>
                : null;
            if (args == null) args = new Dictionary<string, object>();

            var cliOpts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CopyStr(args, cliOpts, "threadId", "thread-id");
            CopyStr(args, cliOpts, "cwd", "cwd");
            CopyStr(args, cliOpts, "title", "title");
            CopyStr(args, cliOpts, "model", "model");
            CopyStr(args, cliOpts, "effort", "effort");
            CopyStr(args, cliOpts, "source", "source");
            CopyStr(args, cliOpts, "focusMode", "focus-mode");
            CopyBoolFlag(args, cliOpts, "refresh", "refresh");
            CopyBoolFlag(args, cliOpts, "resetBreaker", "reset-breaker");
            CopyBoolValue(args, cliOpts, "allowForegroundFallback", "allow-foreground-fallback");
            CopyBoolFlag(args, cliOpts, "allowSessionSwitch", "allow-session-switch");
            CopyBoolValue(args, cliOpts, "allowUnminimize", "allow-unminimize");
            CopyBoolFlag(args, cliOpts, "ignoreUserIdle", "ignore-user-idle");
            CopyInt(args, cliOpts, "ttlHours", "ttl-hours");
            CopyInt(args, cliOpts, "lockTimeoutMs", "lock-timeout-ms");
            CopyInt(args, cliOpts, "userIdleMs", "user-idle-ms");

            string mappedCommand;
            if (toolName == "doctor") mappedCommand = "doctor";
            else if (toolName == "capabilities") mappedCommand = "capabilities";
            else if (toolName == "models") mappedCommand = "models";
            else if (toolName == "query") mappedCommand = "query";
            else if (toolName == "set") mappedCommand = "set";
            else if (toolName == "set_preference") mappedCommand = "set-preference";
            else if (toolName == "create_thread") mappedCommand = "create-thread";
            else
            {
                WriteError(writer, id, -32602, "Unknown tool: " + toolName);
                return;
            }

            int exitCode;
            Dictionary<string, object> payload = Program.ExecuteCommand(mappedCommand, cliOpts, out exitCode);
            bool isError = exitCode != 0 || (payload.ContainsKey("ok") && payload["ok"] is bool && !(bool)payload["ok"]);
            string text = Json.Serialize(payload);

            WriteResult(writer, id, new Dictionary<string, object>
            {
                { "content", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "text" },
                            { "text", text }
                        }
                    }
                },
                { "structuredContent", payload },
                { "isError", isError }
            });
        }

        private static object[] BuildToolDefinitions()
        {
            return new object[]
            {
                ToolDef("doctor",
                    "Read-only diagnostics for CodexDesktopTuning: reports MCP process PID, SessionId, WindowStation, DesktopName, visible Codex Desktop window count, Codex installed/running versions, compatibility cache, circuit breaker state, model cache freshness, and optional thread lock/settings probe.",
                    new Dictionary<string, object>
                    {
                        { "threadId", StrProp("Optional Codex thread UUID to probe (read-only).") },
                        { "resetBreaker", BoolProp("Reset compatibility circuit breaker if previously tripped (default false; doctor is read-only unless true).") }
                    },
                    new string[0]),
                ToolDef("capabilities",
                    "Report this dedicated Codex tuning MCP server's runtime desktop environment (DesktopName, SessionId, accessible Codex window count), supported dual-path capabilities (official app-server for unlocked threads, UIA for Desktop-loaded threads), and safety invariants.",
                    new Dictionary<string, object>(),
                    new string[0]),
                ToolDef("models",
                    "Dynamically list available Codex models and their supported reasoning effort levels via official codex app-server with 24-hour local TTL cache.",
                    new Dictionary<string, object>
                    {
                        { "refresh", BoolProp("Force refresh model catalog immediately instead of using TTL cache.") },
                        { "ttlHours", IntProp("Optional cache TTL override in hours (default 24).") }
                    },
                    new string[0]),
                ToolDef("query",
                    "Query a Codex thread's current model, reasoning effort, and writer-lock status without triggering inference. Defaults to source='backend' (fast, zero UI clicks).",
                    new Dictionary<string, object>
                    {
                        { "threadId", StrProp("Target Codex thread UUID.") },
                        { "title", StrProp("Optional exact conversation title for UIA verification (auto-filled from backend when omitted).") },
                        { "source", EnumProp("Data source: backend (default, zero UI clicks), auto (backend + live UI check), or ui.", new[] { "backend", "auto", "ui" }) }
                    },
                    new[] { "threadId" }),
                ToolDef("set",
                    "Change a Codex thread's model and/or reasoning effort without submitting a prompt or triggering inference. Uses official app-server for unlocked threads, and safe UIA with UUID verification + readback for Desktop-loaded threads.",
                    new Dictionary<string, object>
                    {
                        { "threadId", StrProp("Target Codex thread UUID.") },
                        { "model", StrProp("Optional target model id or display label (e.g. gpt-6.1-sol, gpt-6.2-sol).") },
                        { "effort", StrProp("Optional target reasoning effort (e.g. low, medium, high, xhigh, max, ultra).") },
                        { "title", StrProp("Optional exact conversation title (auto-resolved from backend when omitted).") },
                        { "allowForegroundFallback", BoolProp("Allow brief window activation when effort slider requires keyboard focus (default true). Restores previous foreground window afterward only if Codex was not foreground beforehand and still holds focus; pass false to forbid foreground activation.") },
                        { "allowSessionSwitch", BoolProp("Authorize temporary sidebar switch if target thread is not currently selected in Codex Desktop, restoring original chat afterward (default false).") },
                        { "allowUnminimize", BoolProp("If Codex Desktop is minimized, temporarily restore it (preferring background SW_SHOWNOACTIVATE), perform tuning, and restore minimized state per focusMode (default true; pass false to reject minimized windows).") },
                        { "focusMode", EnumProp("Optional per-call override for post-tuning focus behavior: restore (return to previous window & minimized state if Codex took focus), stay (do not switch back if Codex took focus), or focus_codex (bring Codex Desktop to foreground afterward). Omit to use persisted preference (default 'restore').", new[] { "restore", "stay", "focus_codex" }) },
                        { "ignoreUserIdle", BoolProp("Skip GetLastInputInfo user-idle wait guard (default false).") },
                        { "userIdleMs", IntProp("Minimum user input idle milliseconds before UIA mutation (default 2000).") },
                        { "lockTimeoutMs", IntProp("Optional mutex wait timeout in milliseconds (default 0 fail-fast).") }
                    },
                    new[] { "threadId" }),
                ToolDef("set_preference",
                    "Persist user preferences for codex-tuning MCP (saved in %LOCALAPPDATA%\\CodexDesktopTuning\\preferences.json; takes effect immediately without restarting MCP or changing Skill prompts).",
                    new Dictionary<string, object>
                    {
                        { "focusMode", EnumProp("Persistent post-tuning focus behavior: restore (default, return to previous window & minimized state if Codex took focus), stay (keep focus on Codex if tuning brought it forward, otherwise leave focus untouched), or focus_codex (always bring Codex Desktop to foreground after tuning).", new[] { "restore", "stay", "focus_codex" }) }
                    },
                    new[] { "focusMode" }),
                ToolDef("create_thread",
                    "Create a new persistent, unlocked Codex conversation thread in a given project folder with an exact pre-configured model, reasoning effort, and optional title via official codex app-server (zero inference, zero UI clicks). Immediately unsubscribes so Agent HUD send_message can send Turn 1 directly into the new threadId with the chosen model and effort.",
                    new Dictionary<string, object>
                    {
                        { "cwd", StrProp("Absolute path of an existing project/working directory for the new Codex thread.") },
                        { "model", StrProp("Optional initial model id or display label (e.g. gpt-6.1-sol, gpt-6.2-sol).") },
                        { "effort", StrProp("Optional initial reasoning effort (e.g. low, medium, high, xhigh, max, ultra).") },
                        { "title", StrProp("Optional display title for the new thread.") }
                    },
                    new[] { "cwd" })
            };
        }

        private static Dictionary<string, object> ToolDef(string name, string description, Dictionary<string, object> props, string[] required)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", props },
                { "additionalProperties", false }
            };
            if (required != null && required.Length > 0)
            {
                schema["required"] = required;
            }
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", schema }
            };
        }

        private static Dictionary<string, object> StrProp(string desc)
        {
            return new Dictionary<string, object> { { "type", "string" }, { "description", desc } };
        }

        private static Dictionary<string, object> BoolProp(string desc)
        {
            return new Dictionary<string, object> { { "type", "boolean" }, { "description", desc } };
        }

        private static Dictionary<string, object> IntProp(string desc)
        {
            return new Dictionary<string, object> { { "type", "integer" }, { "description", desc } };
        }

        private static Dictionary<string, object> EnumProp(string desc, string[] values)
        {
            return new Dictionary<string, object> { { "type", "string" }, { "description", desc }, { "enum", values } };
        }

        private static void CopyStr(Dictionary<string, object> src, Dictionary<string, string> dst, string srcKey, string dstKey)
        {
            if (src.ContainsKey(srcKey) && src[srcKey] != null)
            {
                string val = src[srcKey].ToString().Trim();
                if (!string.IsNullOrEmpty(val)) dst[dstKey] = val;
            }
        }

        private static void CopyBoolFlag(Dictionary<string, object> src, Dictionary<string, string> dst, string srcKey, string dstKey)
        {
            if (src.ContainsKey(srcKey) && src[srcKey] != null)
            {
                bool b;
                if (bool.TryParse(src[srcKey].ToString(), out b) && b) dst[dstKey] = "true";
            }
        }

        private static void CopyBoolValue(Dictionary<string, object> src, Dictionary<string, string> dst, string srcKey, string dstKey)
        {
            if (src.ContainsKey(srcKey) && src[srcKey] != null)
            {
                bool b;
                if (bool.TryParse(src[srcKey].ToString(), out b)) dst[dstKey] = b ? "true" : "false";
            }
        }

        private static void CopyInt(Dictionary<string, object> src, Dictionary<string, string> dst, string srcKey, string dstKey)
        {
            if (src.ContainsKey(srcKey) && src[srcKey] != null)
            {
                int n;
                if (int.TryParse(src[srcKey].ToString(), out n)) dst[dstKey] = n.ToString();
            }
        }

        private static void WriteResult(StreamWriter writer, object id, object result)
        {
            writer.WriteLine(Json.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            }));
        }

        private static void WriteError(StreamWriter writer, object id, int code, string message)
        {
            writer.WriteLine(Json.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }
                }
            }));
        }
    }
}
