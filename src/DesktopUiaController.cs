using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexDesktopTuning
{
    public sealed class DesktopUiaController
    {
        private static readonly Dictionary<string, string> EffortLabelToSlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "无", "none" },
            { "最小", "minimal" },
            { "极快", "minimal" },
            { "快", "low" },
            { "快速", "low" },
            { "轻", "low" },
            { "轻度", "low" },
            { "精简", "low" },
            { "低", "low" },
            { "基础", "low" },
            { "low", "low" },
            { "中", "medium" },
            { "标准", "medium" },
            { "均衡", "medium" },
            { "medium", "medium" },
            { "高", "high" },
            { "深度", "high" },
            { "high", "high" },
            { "极高", "xhigh" },
            { "超高", "xhigh" },
            { "更高", "xhigh" },
            { "xhigh", "xhigh" },
            { "最大", "max" },
            { "max", "max" },
            { "ultra", "ultra" },
            { "极致", "ultra" },
            { "超级", "ultra" }
        };

        private static readonly Regex PickerRegex = new Regex(
            @"^(?<model>GPT-[^\s]+(?:\s+(?:Sol|Astra|Terra|Luna|Mini|Nano|Pro|Codex|Spark|Plus|Turbo|Fast))?)\s+(?<effort>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BusyActionRegex = new Regex(
            @"^(停止|排队发送|发送消息|取消运行|继续运行|允许执行|拒绝执行)",
            RegexOptions.Compiled);

        private static readonly Regex DeepLinkRegex = new Regex(
            @"^codex://threads/(?<id>[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ScaleRegex = new Regex(
            @"(?<k>\d+)\s+of\s+(?<n>\d+)\.?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct LASTINPUTINFO
            {
                public uint cbSize;
                public uint dwTime;
            }

            [DllImport("user32.dll")]
            public static extern uint GetClipboardSequenceNumber();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool OpenClipboard(IntPtr hWndNewOwner);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool CloseClipboard();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool EmptyClipboard();

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetClipboardData(uint uFormat);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

            [DllImport("user32.dll")]
            public static extern bool IsClipboardFormatAvailable(uint format);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalLock(IntPtr hMem);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GlobalUnlock(IntPtr hMem);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalFree(IntPtr hMem);

            private const uint CF_UNICODETEXT = 13;
            private const uint GMEM_MOVEABLE = 0x0002;

            public static bool TryGetClipboardText(out string text)
            {
                text = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (!OpenClipboard(IntPtr.Zero))
                    {
                        Thread.Sleep(20);
                        continue;
                    }
                    try
                    {
                        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                        {
                            text = null;
                            return true;
                        }
                        IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                        if (handle == IntPtr.Zero) return false;
                        IntPtr ptr = GlobalLock(handle);
                        if (ptr == IntPtr.Zero) return false;
                        try
                        {
                            text = Marshal.PtrToStringUni(ptr);
                            return true;
                        }
                        finally
                        {
                            GlobalUnlock(handle);
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                return false;
            }

            public static bool TrySetClipboardText(string text)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (!OpenClipboard(IntPtr.Zero))
                    {
                        Thread.Sleep(20);
                        continue;
                    }
                    try
                    {
                        EmptyClipboard();
                        if (text == null) return true;
                        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                        if (hGlobal == IntPtr.Zero) return false;
                        IntPtr ptr = GlobalLock(hGlobal);
                        if (ptr == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                            return false;
                        }
                        try
                        {
                            Marshal.Copy(bytes, 0, ptr, bytes.Length);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }
                        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                            return false;
                        }
                        // Ownership of hGlobal transferred to system
                        return true;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                return false;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            public const int SW_SHOWNOACTIVATE = 4;
            public const int SW_SHOWMINNOACTIVE = 7;
            public const int SW_RESTORE = 9;

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern IntPtr GetProcessWindowStation();

            [DllImport("user32.dll")]
            public static extern IntPtr GetThreadDesktop(uint dwThreadId);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool GetUserObjectInformation(
                IntPtr hObj, int nIndex, byte[] pvInfo, uint nLength, out uint lpnLengthNeeded);

            [DllImport("user32.dll")]
            public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

            public static string GetObjectName(IntPtr handle)
            {
                byte[] buf = new byte[256];
                uint needed;
                if (GetUserObjectInformation(handle, 2, buf, (uint)buf.Length, out needed) && needed >= 2)
                {
                    return Encoding.Unicode.GetString(buf, 0, (int)needed - 2);
                }
                return "";
            }

            public static long GetUserIdleMs()
            {
                var lii = new LASTINPUTINFO();
                lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref lii)) return -1;
                uint tick = (uint)Environment.TickCount;
                return (long)(tick - lii.dwTime);
            }
        }

        private sealed class ViewAnchor
        {
            public AutomationElement Doc;
            public List<AutomationElement> Buttons;
            public AutomationElement Bar;
            public AutomationElement Heading;
            public AutomationElement Editor;
        }

        private sealed class PickerInfo
        {
            public AutomationElement Button;
            public string ModelLabel;
            public string EffortSlug;
        }

        public static DesktopWindowStatus InspectDesktop()
        {
            var status = new DesktopWindowStatus();
            using (Process me = Process.GetCurrentProcess())
            {
                status.ProcessId = me.Id;
                status.SessionId = me.SessionId;
            }
            status.WindowStation = Native.GetObjectName(Native.GetProcessWindowStation());
            status.DesktopName = Native.GetObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
            status.IsInteractiveDefaultDesktop =
                string.Equals(status.WindowStation, "WinSta0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(status.DesktopName, "Default", StringComparison.OrdinalIgnoreCase);
            status.UserIdleMs = Native.GetUserIdleMs();

            Process[] procs = Process.GetProcessesByName("ChatGPT");
            status.ChatGptProcessCount = procs.Length;
            var handles = new List<IntPtr>();
            foreach (Process p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        handles.Add(p.MainWindowHandle);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            status.VisibleWindowCount = handles.Count;
            if (!status.IsInteractiveDefaultDesktop)
            {
                status.WindowState = "isolated_desktop";
                return status;
            }

            if (handles.Count == 0)
            {
                status.WindowState = "not_found";
                return status;
            }

            IntPtr hwnd = handles[0];
            status.MainWindowHandle = hwnd.ToInt64();
            status.IsMinimized = Native.IsIconic(hwnd);
            status.IsForeground = Native.GetForegroundWindow() == hwnd;
            if (status.IsMinimized) status.WindowState = "minimized";
            else if (status.IsForeground) status.WindowState = "foreground";
            else status.WindowState = "background";
            return status;
        }

        private readonly List<AutomationElement> _menuStack = new List<AutomationElement>();
        private IntPtr _handle;
        private AutomationElement _window;
        private ViewAnchor _anchor;
        private bool _mayHaveChanged;
        public Dictionary<string, long> LastTimingsMs { get; private set; }

        public bool MayHaveChanged { get { return _mayHaveChanged; } }

        public UiThreadState QueryUi(Guid expectedThreadId, string title, ModelCatalogCache catalog)
        {
            var desk = InspectDesktop();
            if (!desk.IsInteractiveDefaultDesktop)
            {
                return new UiThreadState
                {
                    Visible = false,
                    Reason = "isolated_desktop:" + desk.DesktopName
                };
            }
            if (desk.VisibleWindowCount != 1)
            {
                return new UiThreadState
                {
                    Visible = false,
                    Reason = desk.VisibleWindowCount == 0 ? "desktop_window_not_found" : "desktop_window_not_unique"
                };
            }
            if (desk.IsMinimized)
            {
                return new UiThreadState
                {
                    Visible = false,
                    Reason = "desktop_window_minimized"
                };
            }

            IntPtr initialForeground = Native.GetForegroundWindow();
            try
            {
                AttachWindow((IntPtr)desk.MainWindowHandle);
                WaitForChromiumHydration();
                if (!HasVisibleDocument(title))
                {
                    return new UiThreadState
                    {
                        Visible = false,
                        ActiveTitle = DetectActiveDocumentTitle(),
                        Reason = "target_thread_not_active_in_ui"
                    };
                }

                _anchor = RequireReadyView(title);
                ConfirmThread(_anchor, expectedThreadId);
                PickerInfo picker = ReadPicker(title, catalog, _anchor);
                ModelInfo resolved = CacheStore.FindModel(catalog, picker.ModelLabel);

                return new UiThreadState
                {
                    Visible = true,
                    Idle = true,
                    ThreadVerified = true,
                    ActiveTitle = title,
                    ModelLabel = picker.ModelLabel,
                    ModelId = resolved != null ? resolved.Id : null,
                    Effort = picker.EffortSlug,
                    Reason = "ok"
                };
            }
            finally
            {
                CleanupMenus();
                RestoreFocusIfStillOnCodex(initialForeground);
            }
        }

        public static bool TryFocusCodexWindow()
        {
            try
            {
                var desk = InspectDesktop();
                if (!desk.IsInteractiveDefaultDesktop || desk.VisibleWindowCount != 1 || desk.MainWindowHandle == 0)
                {
                    return false;
                }
                IntPtr hwnd = (IntPtr)desk.MainWindowHandle;
                if (Native.IsIconic(hwnd))
                {
                    Native.ShowWindow(hwnd, Native.SW_RESTORE);
                }
                return Native.SetForegroundWindow(hwnd);
            }
            catch
            {
                return false;
            }
        }

        public UiThreadState ApplyChangesUi(
            Guid expectedThreadId,
            string title,
            string targetModelId,
            string targetEffort,
            ModelCatalogCache catalog,
            CompatCache compat,
            bool allowForegroundFallback,
            bool allowSessionSwitch,
            bool allowUnminimize,
            string focusMode,
            bool ignoreUserIdle,
            int userIdleThresholdMs)
        {
            var desk = InspectDesktop();
            if (!desk.IsInteractiveDefaultDesktop)
            {
                throw new TuningException("isolated_desktop_no_uia",
                    "Current process is on isolated desktop '" + desk.DesktopName + "' (not WinSta0\\Default); UIA cannot access Codex Desktop.");
            }
            if (desk.VisibleWindowCount != 1)
            {
                throw new TuningException(
                    desk.VisibleWindowCount == 0 ? "desktop_window_not_found" : "desktop_window_not_unique",
                    "Expected exactly 1 Codex Desktop window, found " + desk.VisibleWindowCount);
            }
            if (desk.IsMinimized && !allowUnminimize)
            {
                throw new TuningException("desktop_window_minimized",
                    "Codex Desktop window is minimized and allowUnminimize is false.");
            }

            if (!ignoreUserIdle && desk.UserIdleMs >= 0 && desk.UserIdleMs < userIdleThresholdMs)
            {
                throw new TuningException("user_input_active_backoff",
                    "User interacted " + desk.UserIdleMs + "ms ago (< " + userIdleThresholdMs + "ms); aborting UI automation to avoid race conflict.");
            }

            AttachWindow((IntPtr)desk.MainWindowHandle);
            var timings = new Dictionary<string, long>();
            LastTimingsMs = timings;
            Stopwatch phaseSw = Stopwatch.StartNew();

            string originalTitle = null;
            bool switchedSession = false;
            bool wasMinimized = desk.IsMinimized;
            IntPtr priorForeground = Native.GetForegroundWindow();
            bool stoleForeground = false;

            try
            {
                if (wasMinimized)
                {
                    phaseSw.Restart();
                    // Try restoring behind current foreground window without stealing focus first
                    Native.ShowWindow(_handle, Native.SW_SHOWNOACTIVATE);
                    Stopwatch unminSw = Stopwatch.StartNew();
                    while (unminSw.ElapsedMilliseconds < 400 && Native.IsIconic(_handle))
                    {
                        Thread.Sleep(25);
                    }
                    if (Native.IsIconic(_handle) && allowForegroundFallback)
                    {
                        Native.ShowWindow(_handle, Native.SW_RESTORE);
                        stoleForeground = true;
                        unminSw.Restart();
                        while (unminSw.ElapsedMilliseconds < 500 && Native.IsIconic(_handle))
                        {
                            Thread.Sleep(25);
                        }
                    }
                    if (Native.IsIconic(_handle))
                    {
                        throw new TuningException("desktop_unminimize_failed",
                            "Failed to temporarily restore minimized Codex Desktop window for UIA.");
                    }
                    timings["unminimizeMs"] = phaseSw.ElapsedMilliseconds;
                }

                phaseSw.Restart();
                WaitForChromiumHydration();
                timings["hydrationMs"] = phaseSw.ElapsedMilliseconds;

                phaseSw.Restart();
                if (!HasVisibleDocument(title))
                {
                    if (!allowSessionSwitch)
                    {
                        throw new TuningException("target_thread_not_active_in_ui",
                            "Target thread '" + title + "' is not the currently displayed chat. Pass --allow-session-switch to temporarily switch and restore.");
                    }

                    originalTitle = DetectActiveDocumentTitle();
                    if (!string.IsNullOrEmpty(originalTitle))
                    {
                        EnsureActiveChatSafeToLeave(originalTitle);
                    }
                    SwitchSidebarSession(title);
                    switchedSession = true;
                }

                _anchor = WaitOne(delegate
                {
                    if (HasVisibleDocument(title)) return RequireReadyView(title);
                    return null;
                }, "target_document_not_visible", false);
                timings["readyViewMs"] = phaseSw.ElapsedMilliseconds;

                phaseSw.Restart();
                ConfirmThread(_anchor, expectedThreadId);
                timings["confirmThreadMs"] = phaseSw.ElapsedMilliseconds;

                phaseSw.Restart();
                PickerInfo before = ReadPicker(title, catalog, _anchor);
                timings["readPickerBeforeMs"] = phaseSw.ElapsedMilliseconds;

                string effortModeUsed = compat != null && compat.UiStrategy != null
                    ? compat.UiStrategy.EffortControlMode
                    : "unknown";
                int verifiedScaleTotal = 0;
                AutomationElement reusablePowerMenu = null;

                // 1. Model change (pure UIA native patterns — works in background/occluded!)
                if (!string.IsNullOrEmpty(targetModelId))
                {
                    phaseSw.Restart();
                    ModelInfo targetModel = CacheStore.FindModel(catalog, targetModelId);
                    string targetKey = CacheStore.NormalizeModelKey(targetModel != null ? targetModel.DisplayName : targetModelId);
                    if (CacheStore.NormalizeModelKey(before.ModelLabel) != targetKey)
                    {
                        SetModelUi(
                            before,
                            targetModel != null ? targetModel.DisplayName : targetModelId,
                            title,
                            false);

                        before = WaitOne(delegate
                        {
                            PickerInfo p = TryParsePickerButton(before.Button, catalog, null);
                            if (p != null && CacheStore.NormalizeModelKey(p.ModelLabel) == targetKey) return p;
                            try
                            {
                                PickerInfo full = ReadPicker(title, catalog);
                                if (CacheStore.NormalizeModelKey(full.ModelLabel) == targetKey) return full;
                            }
                            catch (TuningException) { }
                            return null;
                        }, "model_readback_mismatch", true, 3000);
                    }
                    timings["modelUiMs"] = phaseSw.ElapsedMilliseconds;
                }

                // 2. Effort change
                if (!string.IsNullOrEmpty(targetEffort))
                {
                    phaseSw.Restart();
                    string desiredEffort = targetEffort.ToLowerInvariant();
                    if (!string.Equals(before.EffortSlug, desiredEffort, StringComparison.OrdinalIgnoreCase))
                    {
                        ModelInfo activeModel = CacheStore.FindModel(catalog, before.ModelLabel);
                        List<string> orderedEfforts = ComputeOrderedEfforts(activeModel, catalog);
                        List<string> supportedEfforts = ComputeSupportedEfforts(activeModel);
                        if ((orderedEfforts.Count == 0 || !orderedEfforts.Contains(desiredEffort)) &&
                            (supportedEfforts.Count == 0 || !supportedEfforts.Contains(desiredEffort)))
                        {
                            throw new TuningException("effort_not_supported_for_model",
                                "Effort '" + desiredEffort + "' is not in supported/enabled efforts for model '" + before.ModelLabel + "'.");
                        }

                        effortModeUsed = SetEffortUi(
                            before, desiredEffort, orderedEfforts, supportedEfforts, title,
                            allowForegroundFallback, reusablePowerMenu,
                            ref priorForeground, ref stoleForeground, out verifiedScaleTotal);
                        reusablePowerMenu = null;

                        before = WaitOne(delegate
                        {
                            PickerInfo p = TryParsePickerButton(before.Button, catalog, null);
                            if (p != null && string.Equals(p.EffortSlug, desiredEffort, StringComparison.OrdinalIgnoreCase)) return p;
                            try
                            {
                                PickerInfo full = ReadPicker(title, catalog);
                                return string.Equals(full.EffortSlug, desiredEffort, StringComparison.OrdinalIgnoreCase) ? full : null;
                            }
                            catch (TuningException) { return null; }
                        }, "effort_readback_mismatch", true, 3000);
                    }
                    else if (reusablePowerMenu != null)
                    {
                        CloseMenu(before.Button);
                        _menuStack.Remove(before.Button);
                        reusablePowerMenu = null;
                    }
                    timings["effortUiMs"] = phaseSw.ElapsedMilliseconds;
                }

                ModelInfo finalModel = CacheStore.FindModel(catalog, before.ModelLabel);
                if (compat != null)
                {
                    CacheStore.RecordCompatSuccess(
                        compat, effortModeUsed, verifiedScaleTotal,
                        finalModel != null ? finalModel.Id : before.ModelLabel,
                        before.EffortSlug);
                }

                return new UiThreadState
                {
                    Visible = true,
                    Idle = true,
                    ThreadVerified = true,
                    ActiveTitle = title,
                    ModelLabel = before.ModelLabel,
                    ModelId = finalModel != null ? finalModel.Id : null,
                    Effort = before.EffortSlug,
                    EffortControlMode = effortModeUsed,
                    Reason = "ok"
                };
            }
            finally
            {
                CleanupMenus();
                if (switchedSession && !string.IsNullOrEmpty(originalTitle))
                {
                    try { SwitchSidebarSession(originalTitle); } catch { }
                }
                string effectiveFocusMode = CacheStore.NormalizeFocusMode(focusMode) ?? "restore";
                bool endedOnCodex = Native.GetForegroundWindow() == _handle;
                if (effectiveFocusMode == "focus_codex")
                {
                    try
                    {
                        if (Native.IsIconic(_handle)) Native.ShowWindow(_handle, Native.SW_RESTORE);
                        if (Native.GetForegroundWindow() != _handle) Native.SetForegroundWindow(_handle);
                    }
                    catch { }
                }
                else if (effectiveFocusMode == "stay")
                {
                    // Do not restore priorForeground; only re-minimize if Codex never took foreground
                    if (wasMinimized && !endedOnCodex)
                    {
                        try { Native.ShowWindow(_handle, Native.SW_SHOWMINNOACTIVE); } catch { }
                    }
                }
                else // "restore" (default)
                {
                    if (wasMinimized)
                    {
                        try { Native.ShowWindow(_handle, Native.SW_SHOWMINNOACTIVE); } catch { }
                    }
                    if (priorForeground != IntPtr.Zero && priorForeground != _handle &&
                        (endedOnCodex || Native.GetForegroundWindow() == _handle))
                    {
                        try { Native.SetForegroundWindow(priorForeground); } catch { }
                    }
                }
            }
        }

        private void RestoreFocusIfStillOnCodex(IntPtr priorForeground)
        {
            // Only restore focus back to priorForeground if:
            // 1. Codex was NOT the foreground window before our operation, AND
            // 2. Focus is STILL on Codex after our operation finishes.
            // If focus is already on another window (or user started in Codex), do nothing.
            if (priorForeground == IntPtr.Zero || priorForeground == _handle) return;
            try
            {
                if (Native.GetForegroundWindow() == _handle)
                {
                    Native.SetForegroundWindow(priorForeground);
                }
            }
            catch { }
        }

        private void AttachWindow(IntPtr handle)
        {
            _handle = handle;
            _window = AutomationElement.FromHandle(handle);
            _anchor = null;
            _mayHaveChanged = false;
            _menuStack.Clear();
        }

        private void WaitForChromiumHydration()
        {
            // Chromium/Electron hydrates Blink accessibility lazily on the first WM_GETOBJECT.
            // Poll briefly so the very first UIA call after startup or un-minimize sees RootWebArea.
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500)
            {
                if (!string.IsNullOrEmpty(DetectActiveDocumentTitle())) return;
                Thread.Sleep(40);
            }
        }

        private List<AutomationElement> FindByType(AutomationElement root, ControlType controlType, string exactName = null, string automationId = null)
        {
            var conds = new List<Condition>();
            if (controlType != null) conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
            if (!string.IsNullOrEmpty(exactName)) conds.Add(new PropertyCondition(AutomationElement.NameProperty, exactName));
            if (!string.IsNullOrEmpty(automationId)) conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

            Condition finalCond = conds.Count == 0
                ? Condition.TrueCondition
                : (conds.Count == 1 ? conds[0] : new AndCondition(conds.ToArray()));

            var result = new List<AutomationElement>();
            AutomationElementCollection all = root.FindAll(TreeScope.Descendants, finalCond);
            for (int i = 0; i < all.Count; i++) result.Add(all[i]);
            return result;
        }

        private List<AutomationElement> FindVisible(AutomationElement root, ControlType controlType, string exactName = null, string automationId = null)
        {
            var raw = FindByType(root, controlType, exactName, automationId);
            var result = new List<AutomationElement>(raw.Count);
            foreach (var el in raw)
            {
                try
                {
                    if (!el.Current.IsOffscreen) result.Add(el);
                }
                catch { }
            }
            return result;
        }

        private List<AutomationElement> Children(AutomationElement root)
        {
            var result = new List<AutomationElement>();
            AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (int i = 0; i < all.Count; i++) result.Add(all[i]);
            return result;
        }

        private List<AutomationElement> Pick(List<AutomationElement> items, string controlTypeName, string exactName)
        {
            string expectedType = "ControlType." + controlTypeName;
            var result = new List<AutomationElement>();
            foreach (AutomationElement el in items)
            {
                try
                {
                    AutomationElement.AutomationElementInformation cur = el.Current;
                    if (string.Equals(cur.ControlType.ProgrammaticName, expectedType, StringComparison.Ordinal) &&
                        !cur.IsOffscreen &&
                        (string.IsNullOrEmpty(exactName) || string.Equals(cur.Name, exactName, StringComparison.Ordinal)))
                    {
                        result.Add(el);
                    }
                }
                catch { }
            }
            return result;
        }

        private static AutomationElement One(List<AutomationElement> items, string code, bool isCompat)
        {
            if (items == null || items.Count != 1)
            {
                throw new TuningException(code, code + " (count=" + (items == null ? 0 : items.Count) + ")", isCompat);
            }
            return items[0];
        }

        private static T Pattern<T>(AutomationElement el, AutomationPattern kind) where T : BasePattern
        {
            object obj;
            if (!el.Current.IsEnabled || !el.TryGetCurrentPattern(kind, out obj))
            {
                throw new TuningException("control_disabled_or_pattern_missing",
                    "Control disabled or pattern missing: " + kind.ProgrammaticName, true);
            }
            return (T)obj;
        }

        private static bool Same(AutomationElement a, AutomationElement b)
        {
            if (a == null || b == null) return false;
            int[] ra = a.GetRuntimeId();
            int[] rb = b.GetRuntimeId();
            if (ra == null || rb == null || ra.Length != rb.Length) return false;
            for (int i = 0; i < ra.Length; i++) if (ra[i] != rb[i]) return false;
            return true;
        }

        private static T WaitOne<T>(Func<T> finder, string code, bool isCompat, int timeoutMs = 2000) where T : class
        {
            Stopwatch sw = Stopwatch.StartNew();
            do
            {
                try
                {
                    T val = finder();
                    if (val != null) return val;
                }
                catch (TuningException)
                {
                    throw;
                }
                catch { }
                Thread.Sleep(30);
            } while (sw.ElapsedMilliseconds < timeoutMs);
            throw new TuningException(code, code, isCompat);
        }

        private bool HasVisibleDocument(string title)
        {
            return FindVisible(_window, ControlType.Document, title, "RootWebArea").Count == 1;
        }

        private string DetectActiveDocumentTitle()
        {
            foreach (var d in FindVisible(_window, ControlType.Document, null, "RootWebArea"))
            {
                try
                {
                    string name = d.Current.Name;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch { }
            }
            return null;
        }

        private void EnsureActiveChatSafeToLeave(string activeTitle)
        {
            var doc = One(FindVisible(_window, ControlType.Document, activeTitle, "RootWebArea"),
                "active_document_not_unique", false);
            var editors = FindVisible(doc, ControlType.Edit, "和 Codex 一起工作");
            if (editors.Count == 1)
            {
                object vpObj;
                if (editors[0].TryGetCurrentPattern(ValuePattern.Pattern, out vpObj))
                {
                    string draft = (((ValuePattern)vpObj).Current.Value ?? "").Trim();
                    if (!string.IsNullOrEmpty(draft) && !string.Equals(draft, editors[0].Current.Name, StringComparison.Ordinal))
                    {
                        throw new TuningException("active_chat_has_unsent_draft",
                            "Current active chat '" + activeTitle + "' has unsent draft text; refusing to switch sessions.");
                    }
                }
            }
        }

        private static readonly string[] SidebarActionExclusions = new[]
        {
            "删除", "归档", "更多", "置顶", "重命名", "取消", "操作", "关闭",
            "delete", "archive", "more", "pin", "rename", "cancel", "action", "close"
        };

        private static bool IsSidebarActionButtonName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            foreach (string kw in SidebarActionExclusions)
            {
                if (lower.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void SwitchSidebarSession(string targetTitle)
        {
            var rows = FindVisible(_window, ControlType.ListItem, targetTitle);
            if (rows.Count == 0)
            {
                var allRows = FindByType(_window, ControlType.ListItem, targetTitle);
                if (allRows.Count == 1)
                {
                    object scrollObj;
                    if (allRows[0].TryGetCurrentPattern(ScrollItemPattern.Pattern, out scrollObj))
                    {
                        try { ((ScrollItemPattern)scrollObj).ScrollIntoView(); Thread.Sleep(40); } catch { }
                    }
                    rows = allRows;
                }
            }

            var row = One(rows, "target_sidebar_row_not_unique", false);
            object pat;
            if (row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pat))
            {
                ((SelectionItemPattern)pat).Select();
                Thread.Sleep(80);
                return;
            }
            if (row.TryGetCurrentPattern(InvokePattern.Pattern, out pat))
            {
                ((InvokePattern)pat).Invoke();
                Thread.Sleep(80);
                return;
            }

            // Electron/React sidebar ListItem is often a container whose child Button/Hyperlink carries InvokePattern.
            var titleMatched = new List<AutomationElement>();
            var fallbackNav = new List<AutomationElement>();
            var diag = new List<string>();

            foreach (var child in Children(row))
            {
                try
                {
                    var cur = child.Current;
                    string ct = cur.ControlType != null ? cur.ControlType.ProgrammaticName.Replace("ControlType.", "") : "?";
                    string cname = (cur.Name ?? "").Trim();
                    var supportedPats = child.GetSupportedPatterns();
                    var patNames = new List<string>();
                    foreach (var p in supportedPats)
                    {
                        patNames.Add(p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""));
                    }
                    diag.Add("[" + ct + ":" + cname + ":" + string.Join("|", patNames) + "]");

                    if (!cur.IsEnabled || cur.IsOffscreen) continue;
                    if (IsSidebarActionButtonName(cname)) continue;

                    object childPat;
                    bool hasSelect = child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out childPat);
                    bool hasInvoke = child.TryGetCurrentPattern(InvokePattern.Pattern, out childPat);
                    if (!hasSelect && !hasInvoke) continue;

                    if (!string.IsNullOrEmpty(cname) &&
                        (string.Equals(cname, targetTitle, StringComparison.Ordinal) ||
                         cname.IndexOf(targetTitle, StringComparison.Ordinal) >= 0))
                    {
                        titleMatched.Add(child);
                    }
                    else if (cur.ControlType == ControlType.Hyperlink || cur.ControlType == ControlType.Button)
                    {
                        fallbackNav.Add(child);
                    }
                }
                catch { }
            }

            AutomationElement clickTarget = null;
            if (titleMatched.Count == 1) clickTarget = titleMatched[0];
            else if (titleMatched.Count == 0 && fallbackNav.Count == 1) clickTarget = fallbackNav[0];

            if (clickTarget != null)
            {
                object clickPat;
                if (clickTarget.TryGetCurrentPattern(SelectionItemPattern.Pattern, out clickPat))
                {
                    ((SelectionItemPattern)clickPat).Select();
                }
                else if (clickTarget.TryGetCurrentPattern(InvokePattern.Pattern, out clickPat))
                {
                    ((InvokePattern)clickPat).Invoke();
                }
                Thread.Sleep(80);
                return;
            }

            throw new TuningException("sidebar_row_not_selectable",
                "Sidebar ListItem for '" + targetTitle + "' had no unique selectable/invokable target. Children: " + string.Join(", ", diag), true);
        }

        private ViewAnchor RequireReadyView(string title)
        {
            var docs = Pick(Children(_window), "Document", title)
                .Where(d => string.Equals(d.Current.AutomationId, "RootWebArea", StringComparison.Ordinal)).ToList();
            var doc = One(docs, "target_document_not_unique", false);
            var items = Children(doc);
            var bar = One(Pick(items, "ToolBar", "聊天工具栏"), "chat_toolbar_not_unique", true);
            var heading = One(Pick(Children(bar), "Text", title), "chat_title_mismatch", false);
            var row = One(Pick(items, "ListItem", title), "target_sidebar_row_not_unique", false);
            var editor = One(Pick(items, "Edit", "和 Codex 一起工作"), "composer_not_unique", true);
            var buttons = Pick(items, "Button", "");
            var voice = One(buttons.Where(b => string.Equals(b.Current.Name, "开启语音聊天", StringComparison.Ordinal)).ToList(),
                "idle_signal_missing", true);

            var busy = Pick(Children(row), "StatusBar", "").Where(s => !string.IsNullOrEmpty(s.Current.Name)).ToList();
            var actions = buttons.Where(b =>
                b.Current.IsEnabled && BusyActionRegex.IsMatch(b.Current.Name ?? "")).ToList();

            string draft = (Pattern<ValuePattern>(editor, ValuePattern.Pattern).Current.Value ?? "").Trim();
            if (!voice.Current.IsEnabled || busy.Count > 0 || actions.Count > 0 ||
                (!string.IsNullOrEmpty(draft) && !string.Equals(draft, editor.Current.Name, StringComparison.Ordinal)))
            {
                throw new TuningException("chat_not_confirmed_idle", "Target chat is busy or has unsent draft text.");
            }

            if (_anchor != null && (!Same(heading, _anchor.Heading) || !Same(editor, _anchor.Editor)))
            {
                throw new TuningException("chat_view_changed", "Target chat view changed mid-operation.");
            }

            return new ViewAnchor { Doc = doc, Buttons = buttons, Bar = bar, Heading = heading, Editor = editor };
        }

        private void OpenMenu(AutomationElement control)
        {
            var pat = Pattern<ExpandCollapsePattern>(control, ExpandCollapsePattern.Pattern);
            if (pat.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
            {
                throw new TuningException("menu_already_open_or_unavailable", "Menu is already open or unavailable.");
            }
            _menuStack.Add(control);
            pat.Expand();
        }

        private static void CloseMenu(AutomationElement control)
        {
            var pat = Pattern<ExpandCollapsePattern>(control, ExpandCollapsePattern.Pattern);
            if (pat.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                pat.Collapse();
            }
        }

        private void CleanupMenus()
        {
            for (int i = _menuStack.Count - 1; i >= 0; i--)
            {
                try { CloseMenu(_menuStack[i]); } catch { }
            }
            _menuStack.Clear();
        }

        private void ConfirmThread(ViewAnchor view, Guid expectedThreadId)
        {
            OpenMenu(One(FindVisible(view.Bar, ControlType.Button, "聊天操作"), "chat_actions_not_unique", true));
            var menu = WaitOne(delegate
            {
                var m = FindVisible(_window, ControlType.Menu, "聊天操作");
                return m.Count == 1 ? m[0] : null;
            }, "chat_actions_missing", true);

            OpenMenu(One(FindVisible(menu, ControlType.MenuItem, "复制"), "copy_menu_not_unique", true));
            var copy = WaitOne(delegate
            {
                var c = FindVisible(_window, ControlType.MenuItem, "复制深度链接");
                return c.Count == 1 ? c[0] : null;
            }, "copy_deep_link_missing", true);

            // Allow Electron submenu transition to finish binding before invoking
            Thread.Sleep(30);

            // Snapshot clipboard text via pure Win32 (zero OLE/message-pump issues in persistent --mcp process)
            uint prior = Native.GetClipboardSequenceNumber();
            string savedText;
            bool hadText = Native.TryGetClipboardText(out savedText) && savedText != null;

            if (Native.GetClipboardSequenceNumber() != prior)
            {
                throw new TuningException("clipboard_changed", "Clipboard changed before deep-link copy.");
            }

            bool restored = false;
            try
            {
                Pattern<InvokePattern>(copy, InvokePattern.Pattern).Invoke();

                Guid copiedId = Guid.Empty;
                uint copiedSeq = 0;
                bool retried = false;
                string lastSeenText = null;
                Stopwatch sw = Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < 3500)
                {
                    uint seq = Native.GetClipboardSequenceNumber();
                    if (seq != prior)
                    {
                        string text;
                        if (Native.TryGetClipboardText(out text))
                        {
                            lastSeenText = text;
                            if (Native.GetClipboardSequenceNumber() == seq)
                            {
                                Match m = DeepLinkRegex.Match(text ?? "");
                                if (m.Success)
                                {
                                    copiedId = new Guid(m.Groups["id"].Value);
                                    copiedSeq = seq;
                                    break;
                                }
                            }
                        }
                    }
                    else if (!retried && sw.ElapsedMilliseconds >= 400)
                    {
                        retried = true;
                        try { Pattern<InvokePattern>(copy, InvokePattern.Pattern).Invoke(); } catch { }
                    }
                    Thread.Sleep(25);
                }

                if (copiedId == Guid.Empty)
                {
                    // Fallback: if clipboard already held the exact deep link before Invoke and Electron skipped redundant write
                    if (hadText && !string.IsNullOrEmpty(savedText))
                    {
                        Match priorMatch = DeepLinkRegex.Match(savedText);
                        if (priorMatch.Success && new Guid(priorMatch.Groups["id"].Value) == expectedThreadId)
                        {
                            copiedId = expectedThreadId;
                            copiedSeq = prior;
                        }
                    }
                }

                if (copiedId == Guid.Empty)
                {
                    string diag = Native.GetClipboardSequenceNumber() == prior
                        ? "seq_unchanged"
                        : ("unexpected_text:" + (lastSeenText ?? "<null>"));
                    throw new TuningException("deep_link_not_copied_or_clipboard_changed",
                        "Deep link verification failed (" + diag + ")", true);
                }

                if (Native.GetClipboardSequenceNumber() == copiedSeq && copiedSeq != prior)
                {
                    if (hadText)
                    {
                        restored = Native.TrySetClipboardText(savedText);
                    }
                    else
                    {
                        restored = Native.TrySetClipboardText(null);
                    }
                }

                if (copiedId != expectedThreadId)
                {
                    throw new TuningException("thread_id_mismatch",
                        "UI deep link thread ID " + copiedId + " did not match expected " + expectedThreadId);
                }
            }
            finally
            {
                if (!restored && hadText && Native.GetClipboardSequenceNumber() != prior)
                {
                    Native.TrySetClipboardText(savedText);
                }
                CleanupMenus();
            }
        }

        private PickerInfo ReadPicker(string title, ModelCatalogCache catalog, ViewAnchor reuseView = null, AutomationElement openPowerMenu = null)
        {
            var view = reuseView ?? RequireReadyView(title);
            var candidates = new List<AutomationElement>();
            foreach (var b in view.Buttons)
            {
                try
                {
                    string name = (b.Current.Name ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    object expObj;
                    if (!b.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expObj)) continue;
                    if (PickerRegex.IsMatch(name) || MatchesCatalogModelPrefix(name, catalog))
                    {
                        candidates.Add(b);
                    }
                }
                catch { }
            }

            var button = One(candidates, "picker_not_unique_or_unknown_label", true);
            PickerInfo parsed = TryParsePickerButton(button, catalog, openPowerMenu);
            if (parsed != null) return parsed;

            string fullName = (button.Current.Name ?? "").Trim();
            string modelLabel, effortLabel;
            if (!TrySplitPickerName(fullName, catalog, out modelLabel, out effortLabel))
            {
                Match m = PickerRegex.Match(fullName);
                if (m.Success)
                {
                    modelLabel = m.Groups["model"].Value.Trim();
                    effortLabel = m.Groups["effort"].Value.Trim();
                }
            }
            if (string.IsNullOrEmpty(modelLabel) || string.IsNullOrEmpty(effortLabel))
            {
                throw new TuningException("picker_not_unique_or_unknown_label",
                    "Could not parse picker button label: '" + fullName + "'", true);
            }

            // Dynamically resolve unrecognized localized effort label via Power menu "<k> of <n>" scale!
            string slug = ResolveEffortSlugFromPowerMenu(button, modelLabel, catalog);
            if (string.IsNullOrEmpty(slug))
            {
                throw new TuningException("unknown_effort_label",
                    "Unrecognized effort label '" + effortLabel + "' in picker '" + fullName + "'", true);
            }

            return new PickerInfo
            {
                Button = button,
                ModelLabel = modelLabel,
                EffortSlug = slug
            };
        }

        private PickerInfo TryParsePickerButton(AutomationElement button, ModelCatalogCache catalog, AutomationElement openPowerMenu)
        {
            try
            {
                string fullName = (button.Current.Name ?? "").Trim();
                string modelLabel = null;
                string effortLabel = null;
                if (!TrySplitPickerName(fullName, catalog, out modelLabel, out effortLabel))
                {
                    Match m = PickerRegex.Match(fullName);
                    if (m.Success)
                    {
                        modelLabel = m.Groups["model"].Value.Trim();
                        effortLabel = m.Groups["effort"].Value.Trim();
                    }
                }
                if (string.IsNullOrEmpty(modelLabel) || string.IsNullOrEmpty(effortLabel)) return null;

                string slug;
                if (!EffortLabelToSlug.TryGetValue(effortLabel, out slug))
                {
                    if (openPowerMenu != null)
                    {
                        slug = ResolveEffortSlugFromOpenPowerMenu(openPowerMenu, modelLabel, catalog);
                    }
                    if (string.IsNullOrEmpty(slug)) return null;
                }

                return new PickerInfo
                {
                    Button = button,
                    ModelLabel = modelLabel,
                    EffortSlug = slug
                };
            }
            catch
            {
                return null;
            }
        }

        private string ResolveEffortSlugFromOpenPowerMenu(AutomationElement power, string modelLabel, ModelCatalogCache catalog)
        {
            int k, n;
            if (TryReadScale(power, out k, out n))
            {
                ModelInfo model = CacheStore.FindModel(catalog, modelLabel);
                List<string> ordered = ComputeOrderedEfforts(model, catalog);
                if (n == ordered.Count && k >= 1 && k <= ordered.Count)
                {
                    return ordered[k - 1];
                }
                List<string> supported = ComputeSupportedEfforts(model);
                if (n == supported.Count && k >= 1 && k <= supported.Count)
                {
                    return supported[k - 1];
                }
            }
            return null;
        }

        private static bool MatchesCatalogModelPrefix(string buttonName, ModelCatalogCache catalog)
        {
            string m, e;
            return TrySplitPickerName(buttonName, catalog, out m, out e);
        }

        private static bool TrySplitPickerName(string buttonName, ModelCatalogCache catalog, out string modelLabel, out string effortLabel)
        {
            modelLabel = null;
            effortLabel = null;
            if (string.IsNullOrEmpty(buttonName) || catalog == null || catalog.Models == null) return false;

            string normFull = CacheStore.NormalizeModelKey(buttonName);
            ModelInfo bestModel = null;
            int bestLen = 0;

            foreach (var model in catalog.Models)
            {
                string normDisp = CacheStore.NormalizeModelKey(model.DisplayName);
                if (!string.IsNullOrEmpty(normDisp) && normFull.StartsWith(normDisp, StringComparison.Ordinal) &&
                    normDisp.Length > bestLen && normFull.Length > normDisp.Length)
                {
                    bestModel = model;
                    bestLen = normDisp.Length;
                }
            }

            if (bestModel == null) return false;

            // Walk characters of buttonName until we consume bestLen normalized chars
            int consumed = 0;
            int splitIdx = -1;
            for (int i = 0; i < buttonName.Length; i++)
            {
                char c = buttonName[i];
                if (!char.IsWhiteSpace(c) && c != '-' && c != '_') consumed++;
                if (consumed == bestLen)
                {
                    splitIdx = i + 1;
                    break;
                }
            }
            if (splitIdx <= 0 || splitIdx >= buttonName.Length) return false;

            modelLabel = buttonName.Substring(0, splitIdx).Trim();
            effortLabel = buttonName.Substring(splitIdx).Trim();
            return !string.IsNullOrEmpty(modelLabel) && !string.IsNullOrEmpty(effortLabel);
        }

        private string ResolveEffortSlugFromPowerMenu(AutomationElement pickerButton, string modelLabel, ModelCatalogCache catalog)
        {
            try
            {
                OpenMenu(pickerButton);
                var power = WaitOne(delegate
                {
                    var m = FindVisible(_window, ControlType.Menu, "选择推理强度");
                    return m.Count == 1 ? m[0] : null;
                }, "power_menu_missing", true);

                return ResolveEffortSlugFromOpenPowerMenu(power, modelLabel, catalog);
            }
            catch { }
            finally
            {
                try
                {
                    CloseMenu(pickerButton);
                    _menuStack.Remove(pickerButton);
                }
                catch { }
            }
            return null;
        }

        private AutomationElement OpenPowerMenu(PickerInfo from)
        {
            OpenMenu(from.Button);
            return WaitOne(delegate
            {
                var m = FindVisible(_window, ControlType.Menu, "选择推理强度");
                return m.Count == 1 ? m[0] : null;
            }, "power_menu_missing", true);
        }

        private AutomationElement SetModelUi(PickerInfo from, string targetModelDisplayOrId, string title, bool keepPowerMenuOpen)
        {
            string pickerName = from.Button.Current.Name;
            AutomationElement power = OpenPowerMenu(from);
            var switchItem = One(FindVisible(power, ControlType.MenuItem, "选择模型"), "model_list_switch_not_unique", true);
            Pattern<InvokePattern>(switchItem, InvokePattern.Pattern).Invoke();

            var menu = WaitOne(delegate
            {
                var m = FindVisible(_window, ControlType.Menu, pickerName);
                return m.Count == 1 ? m[0] : null;
            }, "model_list_missing", true);

            Thread.Sleep(40);

            string targetKey = CacheStore.NormalizeModelKey(targetModelDisplayOrId);
            var seenNames = new List<string>();
            AutomationElement modelRadio = FindModelOptionInMenu(menu, targetKey, seenNames);

            // If not at top level, inspect MenuItem/Button children (e.g. "更多模型" / "旧版模型" / "Legacy" / "其他模型")
            if (modelRadio == null)
            {
                var expandables = new List<AutomationElement>();
                expandables.AddRange(FindByType(menu, ControlType.MenuItem));
                expandables.AddRange(FindByType(menu, ControlType.Button));
                foreach (var child in expandables)
                {
                    try
                    {
                        string ct = child.Current.ControlType.ProgrammaticName;
                        string cname = (child.Current.Name ?? "").Trim();
                        string tag = "[" + ct.Replace("ControlType.", "") + ":" + cname + (child.Current.IsOffscreen ? ":off" : "") + "]";
                        if (!string.IsNullOrEmpty(cname) && !seenNames.Contains(tag)) seenNames.Add(tag);

                        object expObj;
                        if (child.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expObj))
                        {
                            var exp = (ExpandCollapsePattern)expObj;
                            if (exp.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                            {
                                OpenMenu(child);
                                Thread.Sleep(80);
                                var allMenus = FindVisible(_window, ControlType.Menu);
                                foreach (var subMenu in allMenus)
                                {
                                    if (Same(subMenu, menu)) continue;
                                    modelRadio = FindModelOptionInMenu(subMenu, targetKey, seenNames);
                                    if (modelRadio != null) break;
                                }
                                if (modelRadio != null) break;
                            }
                        }
                        else if (cname.IndexOf("更多", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 cname.IndexOf("全部", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 cname.IndexOf("旧版", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 cname.IndexOf("Legacy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 cname.IndexOf("More", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            object invObj;
                            if (child.TryGetCurrentPattern(InvokePattern.Pattern, out invObj))
                            {
                                ((InvokePattern)invObj).Invoke();
                                Thread.Sleep(80);
                                modelRadio = FindModelOptionInMenu(menu, targetKey, seenNames);
                                if (modelRadio != null) break;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (modelRadio == null)
            {
                throw new TuningException("model_unavailable_or_ambiguous",
                    "Model '" + targetModelDisplayOrId + "' not found in UI picker. Discovered options: " + string.Join(", ", seenNames), false);
            }

            // Scroll into view if offscreen inside scrollable menu
            object scrollObj;
            if (modelRadio.Current.IsOffscreen && modelRadio.TryGetCurrentPattern(ScrollItemPattern.Pattern, out scrollObj))
            {
                try { ((ScrollItemPattern)scrollObj).ScrollIntoView(); Thread.Sleep(30); } catch { }
            }

            var sel = Pattern<SelectionItemPattern>(modelRadio, SelectionItemPattern.Pattern);
            if (sel.Current.IsSelected)
            {
                throw new TuningException("model_state_uncertain", "Model radio button already selected.");
            }
            RequireReadyView(title);
            _mayHaveChanged = true;
            sel.Select();
            var powerAfter = WaitOne(delegate
            {
                var m = FindVisible(_window, ControlType.Menu, "选择推理强度");
                return m.Count == 1 ? m[0] : null;
            }, "model_selection_unconfirmed", true);

            if (keepPowerMenuOpen)
            {
                return powerAfter;
            }

            CloseMenu(from.Button);
            _menuStack.Remove(from.Button);
            return null;
        }

        private AutomationElement FindModelOptionInMenu(AutomationElement menu, string targetKey, List<string> seenNames)
        {
            var exact = new List<AutomationElement>();
            var prefix = new List<AutomationElement>();
            foreach (var r in FindByType(menu, ControlType.RadioButton))
            {
                try
                {
                    var cur = r.Current;
                    string raw = (cur.Name ?? "").Trim();
                    string label = cur.IsOffscreen ? (raw + " [offscreen]") : raw;
                    if (!string.IsNullOrEmpty(raw) && !seenNames.Contains(label)) seenNames.Add(label);
                    string norm = CacheStore.NormalizeModelKey(raw);
                    if (norm == targetKey) exact.Add(r);
                    else if (norm.StartsWith(targetKey, StringComparison.Ordinal)) prefix.Add(r);
                }
                catch { }
            }
            if (exact.Count == 1) return exact[0];
            if (exact.Count == 0 && prefix.Count == 1) return prefix[0];
            return null;
        }

        private static List<string> ComputeSupportedEfforts(ModelInfo model)
        {
            var supported = new List<string>();
            if (model != null && model.SupportedReasoningEfforts != null)
            {
                foreach (var e in model.SupportedReasoningEfforts)
                {
                    if (!string.IsNullOrEmpty(e.ReasoningEffort))
                    {
                        string slug = e.ReasoningEffort.ToLowerInvariant();
                        if (!supported.Contains(slug)) supported.Add(slug);
                    }
                }
            }
            return supported;
        }

        private static List<string> ComputeOrderedEfforts(ModelInfo model, ModelCatalogCache catalog)
        {
            List<string> supported = ComputeSupportedEfforts(model);
            if (catalog != null && catalog.EnabledDesktopEfforts != null && catalog.EnabledDesktopEfforts.Count > 0)
            {
                var intersected = new List<string>();
                foreach (string e in catalog.EnabledDesktopEfforts)
                {
                    if (supported.Count == 0 || supported.Contains(e)) intersected.Add(e);
                }
                if (intersected.Count > 0) return intersected;
            }
            return supported;
        }

        private string SetEffortUi(
            PickerInfo from,
            string desiredEffort,
            List<string> orderedEfforts,
            List<string> supportedEfforts,
            string title,
            bool allowForegroundFallback,
            AutomationElement existingPowerMenu,
            ref IntPtr priorForeground,
            ref bool stoleForeground,
            out int verifiedScaleTotal)
        {
            verifiedScaleTotal = 0;
            AutomationElement power = existingPowerMenu ?? OpenPowerMenu(from);
            var powerChildren = Children(power);

            // Probe 1: Check for native RangeValuePattern (slider) inside Power menu (works in background!)
            foreach (var el in powerChildren)
            {
                object rvObj;
                if (el.Current.IsEnabled && el.TryGetCurrentPattern(RangeValuePattern.Pattern, out rvObj))
                {
                    var rv = (RangeValuePattern)rvObj;
                    int idx = orderedEfforts.IndexOf(desiredEffort);
                    if (idx >= 0 && !rv.Current.IsReadOnly)
                    {
                        double targetVal = rv.Current.Minimum + idx * Math.Max(1.0, rv.Current.SmallChange);
                        if (targetVal <= rv.Current.Maximum)
                        {
                            RequireReadyView(title);
                            _mayHaveChanged = true;
                            rv.SetValue(targetVal);
                            verifiedScaleTotal = orderedEfforts.Count;
                            CloseMenu(from.Button);
                            _menuStack.Remove(from.Button);
                            return "native_range";
                        }
                    }
                }
            }

            // Probe 2: Check for direct RadioButton / MenuItem selection matching the effort
            var radios = Pick(powerChildren, "RadioButton", "");
            foreach (var r in radios)
            {
                string slug;
                if (EffortLabelToSlug.TryGetValue((r.Current.Name ?? "").Trim(), out slug) &&
                    string.Equals(slug, desiredEffort, StringComparison.OrdinalIgnoreCase))
                {
                    var sel = Pattern<SelectionItemPattern>(r, SelectionItemPattern.Pattern);
                    RequireReadyView(title);
                    _mayHaveChanged = true;
                    sel.Select();
                    verifiedScaleTotal = radios.Count;
                    CloseMenu(from.Button);
                    _menuStack.Remove(from.Button);
                    return "native_selection";
                }
            }

            // Probe 3: Verified Codex Desktop Power menu layout (MenuItem '强度' + Text "<Model> <label>, <k> of <n>.")
            var control = One(Pick(powerChildren, "MenuItem", "强度"), "power_control_not_unique", true);
            int currentK, totalN;
            if (!TryReadScale(power, out currentK, out totalN))
            {
                throw new TuningException("power_scale_not_verified", "Could not read '<k> of <n>' scale text from Power menu.", true);
            }

            verifiedScaleTotal = totalN;
            List<string> activeScale = orderedEfforts;
            if (totalN != activeScale.Count)
            {
                // Fallback to the model's full supported effort list if it matches BOTH the live UI step count
                // AND the current effort position (e.g. when config.toml enabled-reasoning-efforts differs from
                // the live UI scale such as 'max' being added/removed dynamically).
                if (supportedEfforts != null && totalN == supportedEfforts.Count &&
                    supportedEfforts.IndexOf(from.EffortSlug) + 1 == currentK)
                {
                    activeScale = supportedEfforts;
                }
                else
                {
                    throw new TuningException("power_scale_length_mismatch",
                        "UI scale has " + totalN + " steps, but catalog/config has " + orderedEfforts.Count +
                        " (model supported: " + (supportedEfforts != null ? supportedEfforts.Count : 0) + ") efforts.", true);
                }
            }

            int currentExpectedK = activeScale.IndexOf(from.EffortSlug) + 1;
            int targetK = activeScale.IndexOf(desiredEffort) + 1;
            if (targetK <= 0)
            {
                throw new TuningException("effort_not_supported_for_model",
                    "Effort '" + desiredEffort + "' is not present on the active UI effort scale (" + string.Join(", ", activeScale) + ").");
            }
            if (currentExpectedK <= 0 || currentK != currentExpectedK)
            {
                throw new TuningException("power_scale_not_verified",
                    "UI scale position " + currentK + "/" + totalN + " did not match expected " + currentExpectedK + " for '" + from.EffortSlug + "'.", true);
            }

            // Check foreground requirement before any SendKeys fallback
            if (Native.GetForegroundWindow() != _handle)
            {
                if (!allowForegroundFallback)
                {
                    throw new TuningException("effort_requires_foreground_or_fallback",
                        "Codex Desktop effort control ('强度') requires keyboard focus; window is in background. Pass --allow-foreground-fallback to authorize temporary activation.");
                }
                // Close menu before activating window so Electron/Chromium does not dismiss the popup mid-focus,
                // then reopen Power menu cleanly in the foreground (matching verified Codex-DesktopTuning.ps1 flow).
                CloseMenu(from.Button);
                _menuStack.Remove(from.Button);
                priorForeground = Native.GetForegroundWindow();
                Native.SetForegroundWindow(_handle);
                stoleForeground = true;
                Stopwatch fgSw = Stopwatch.StartNew();
                while (fgSw.ElapsedMilliseconds < 350 && Native.GetForegroundWindow() != _handle)
                {
                    Thread.Sleep(25);
                }
                if (Native.GetForegroundWindow() != _handle)
                {
                    throw new TuningException("foreground_activation_failed", "Failed to bring Codex Desktop window to foreground.");
                }
                power = OpenPowerMenu(from);
                control = One(FindVisible(power, ControlType.MenuItem, "强度"), "power_control_not_unique", true);
                if (!Same(AutomationElement.FocusedElement, control))
                {
                    try { control.SetFocus(); } catch { }
                    Thread.Sleep(30);
                }
            }

            int steps = targetK - currentK;
            string key = steps > 0 ? "{RIGHT}" : "{LEFT}";
            int stepCount = Math.Abs(steps);
            int runningK = currentK;

            for (int s = 0; s < stepCount; s++)
            {
                if (s == 0) RequireReadyView(title);
                if (!control.Current.IsEnabled || Native.GetForegroundWindow() != _handle ||
                    !Same(AutomationElement.FocusedElement, control))
                {
                    try { control.SetFocus(); } catch { }
                    Thread.Sleep(25);
                    if (!Same(AutomationElement.FocusedElement, control))
                    {
                        throw new TuningException("power_control_not_focused",
                            "Effort control lost focus before step " + (s + 1) + ".", false, _mayHaveChanged);
                    }
                }

                int nextExpectedK = runningK + (steps > 0 ? 1 : -1);
                _mayHaveChanged = true;
                SendKeys.SendWait(key);

                WaitOne(delegate
                {
                    int k, n;
                    if (TryReadScale(power, out k, out n) && k == nextExpectedK && n == totalN) return "ok";
                    return null;
                }, "power_change_unconfirmed", true);
                runningK = nextExpectedK;
            }

            CloseMenu(from.Button);
            _menuStack.Remove(from.Button);
            return "keyboard_slider";
        }

        private bool TryReadScale(AutomationElement power, out int k, out int n)
        {
            k = 0; n = 0;
            foreach (var t in FindVisible(power, ControlType.Text))
            {
                Match m = ScaleRegex.Match(t.Current.Name ?? "");
                if (m.Success)
                {
                    k = int.Parse(m.Groups["k"].Value);
                    n = int.Parse(m.Groups["n"].Value);
                    return true;
                }
            }
            return false;
        }
    }
}
