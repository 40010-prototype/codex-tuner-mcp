using System;
using System.Collections.Generic;

namespace CodexDesktopTuning
{
    public sealed class VersionFingerprint
    {
        public string InstalledPackageVersion { get; set; }
        public string InstalledPackagePath { get; set; }
        public string RunningExePath { get; set; }
        public string RunningProductVersion { get; set; }
        public string RunningFileVersion { get; set; }
        public string OwlAppVersion { get; set; }
        public string CodexCliPath { get; set; }
        public string CodexCliVersion { get; set; }
        public string UiLocale { get; set; }

        public string Key()
        {
            return string.Join("|", new[]
            {
                InstalledPackageVersion ?? "",
                RunningFileVersion ?? "",
                OwlAppVersion ?? "",
                CodexCliVersion ?? "",
                UiLocale ?? ""
            });
        }
    }

    public sealed class EffortOption
    {
        public string ReasoningEffort { get; set; }
        public string Description { get; set; }
    }

    public sealed class ModelInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public bool IsDefault { get; set; }
        public bool Hidden { get; set; }
        public string DefaultReasoningEffort { get; set; }
        public List<EffortOption> SupportedReasoningEfforts { get; set; }
    }

    public sealed class ModelCatalogCache
    {
        public string UpdatedAtUtc { get; set; }
        public int TtlSeconds { get; set; }
        public string CodexCliVersion { get; set; }
        public string DefaultModel { get; set; }
        public string DefaultEffort { get; set; }
        public List<string> EnabledDesktopEfforts { get; set; }
        public List<ModelInfo> Models { get; set; }
    }

    public sealed class UiStrategyCache
    {
        public string StrategyVersion { get; set; }
        public string PickerVerifiedAtUtc { get; set; }
        public string EffortControlMode { get; set; } // "native_range" | "native_selection" | "native_value" | "keyboard_slider" | "unknown"
        public int VerifiedEffortScaleTotal { get; set; }
        public List<string> VerifiedModels { get; set; }
        public List<string> VerifiedEfforts { get; set; }
    }

    public sealed class CircuitBreakerState
    {
        public bool Tripped { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string LastErrorCode { get; set; }
        public string LastErrorMessage { get; set; }
        public string TrippedAtUtc { get; set; }
        public string TrippedFingerprintKey { get; set; }
    }

    public sealed class CompatCache
    {
        public VersionFingerprint Fingerprint { get; set; }
        public string FingerprintKey { get; set; }
        public string LastCheckedAtUtc { get; set; }
        public UiStrategyCache UiStrategy { get; set; }
        public CircuitBreakerState CircuitBreaker { get; set; }
    }

    public sealed class ThreadBackendState
    {
        public string ThreadId { get; set; }
        public string Name { get; set; }
        public string Model { get; set; }
        public string ReasoningEffort { get; set; }
        public string Cwd { get; set; }
        public string RolloutPath { get; set; }
        public string CliVersion { get; set; }
        public string StatusType { get; set; }
        public bool WriterLockHeld { get; set; }
        public long UpdatedAt { get; set; }
        public string Source { get; set; }
    }

    public sealed class DesktopWindowStatus
    {
        public int ProcessId { get; set; }
        public int SessionId { get; set; }
        public string WindowStation { get; set; }
        public string DesktopName { get; set; }
        public bool IsInteractiveDefaultDesktop { get; set; }
        public int ChatGptProcessCount { get; set; }
        public int VisibleWindowCount { get; set; }
        public long MainWindowHandle { get; set; }
        public string WindowState { get; set; } // "foreground" | "background" | "minimized" | "not_found" | "isolated_desktop"
        public bool IsForeground { get; set; }
        public bool IsMinimized { get; set; }
        public long UserIdleMs { get; set; }
    }

    public sealed class UiThreadState
    {
        public bool Visible { get; set; }
        public bool Idle { get; set; }
        public bool ThreadVerified { get; set; }
        public string ActiveTitle { get; set; }
        public string ModelLabel { get; set; }
        public string ModelId { get; set; }
        public string Effort { get; set; }
        public string EffortControlMode { get; set; }
        public string Reason { get; set; }
    }

    public sealed class UserPreferences
    {
        public string FocusMode { get; set; } // "restore" (default) | "stay" | "focus_codex"
        public string UpdatedAtUtc { get; set; }
    }

    public sealed class TuningException : Exception
    {
        public string Code { get; private set; }
        public bool IsCompatError { get; private set; }
        public bool MayHaveChanged { get; set; }

        public TuningException(string code, string message, bool isCompatError = false, bool mayHaveChanged = false)
            : base(string.IsNullOrEmpty(message) ? code : message)
        {
            Code = code;
            IsCompatError = isCompatError;
            MayHaveChanged = mayHaveChanged;
        }
    }
}
