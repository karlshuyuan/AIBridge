using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace AIBridge.Editor
{
    /// <summary>
    /// Get console logs from Unity Editor
    /// </summary>
    public class GetLogsCommand : ICommand
    {
        public string Type => "get_logs";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `get_logs` - Get Console Logs

```bash
$CLI get_logs [--count 100] [--logType Error|Warning]
```";

        public CommandResult Execute(CommandRequest request)
        {
            var count = request.GetParam("count", 50);
            var logType = request.GetParam("logType", "all");

            try
            {
                var logs = GetConsoleLogs(count, logType);
                return CommandResult.Success(request.id, new
                {
                    logs = logs,
                    count = logs.Count
                });
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private List<LogEntry> GetConsoleLogs(int maxCount, string logTypeFilter)
        {
            var logs = new List<LogEntry>();

            try
            {
                var consoleReflection = ResolveConsoleReflection();
                if (consoleReflection == null)
                {
                    return logs;
                }

                var totalCount = (int)consoleReflection.GetCountMethod.Invoke(null, null);
                if (totalCount == 0)
                {
                    return logs;
                }

                consoleReflection.StartGettingEntriesMethod.Invoke(null, null);

                try
                {
                    var startIndex = Math.Max(0, totalCount - maxCount);
                    for (var i = startIndex; i < totalCount; i++)
                    {
                        var entry = Activator.CreateInstance(consoleReflection.LogEntryType);
                        var success = (bool)consoleReflection.GetEntryInternalMethod.Invoke(null, new object[] { i, entry });

                        if (success)
                        {
                            var message = GetLogMessage(consoleReflection, entry);
                            var mode = (int)(consoleReflection.ModeField.GetValue(entry) ?? 0);
                            var entryType = GetLogType(mode);

                            if (!ShouldIncludeLog(logTypeFilter, entryType))
                            {
                                continue;
                            }

                            logs.Add(new LogEntry
                            {
                                message = message,
                                type = entryType
                            });
                        }
                    }
                }
                finally
                {
                    consoleReflection.EndGettingEntriesMethod.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to get console logs: {ex.Message}");
            }

            return logs;
        }

        /// <summary>
        /// 解析 Unity Console 反射入口，兼容 UnityEditor 与 UnityEditorInternal 命名空间差异。
        /// </summary>
        private ConsoleReflection ResolveConsoleReflection()
        {
            var editorAssembly = Assembly.GetAssembly(typeof(UnityEditor.Editor));
            if (editorAssembly == null)
            {
                AIBridgeLogger.LogError("Failed to resolve UnityEditor assembly for get_logs.");
                return null;
            }

            var logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries")
                ?? editorAssembly.GetType("UnityEditorInternal.LogEntries");
            var logEntryType = editorAssembly.GetType("UnityEditor.LogEntry")
                ?? editorAssembly.GetType("UnityEditorInternal.LogEntry");

            if (logEntriesType == null || logEntryType == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console reflection types for get_logs.");
                return null;
            }

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var getCountMethod = logEntriesType.GetMethod("GetCount", flags);
            var startGettingEntriesMethod = logEntriesType.GetMethod("StartGettingEntries", flags);
            var endGettingEntriesMethod = logEntriesType.GetMethod("EndGettingEntries", flags);
            var getEntryInternalMethod = logEntriesType.GetMethod("GetEntryInternal", flags);

            if (getCountMethod == null || startGettingEntriesMethod == null || endGettingEntriesMethod == null || getEntryInternalMethod == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console reflection methods for get_logs.");
                return null;
            }

            var fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var conditionField = logEntryType.GetField("condition", fieldFlags);
            var messageField = logEntryType.GetField("message", fieldFlags);
            var modeField = logEntryType.GetField("mode", fieldFlags);

            if (modeField == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console log mode field for get_logs.");
                return null;
            }

            if (conditionField == null && messageField == null)
            {
                AIBridgeLogger.LogError("Failed to resolve Unity console message field for get_logs.");
                return null;
            }

            var consoleReflection = new ConsoleReflection();
            consoleReflection.LogEntryType = logEntryType;
            consoleReflection.GetCountMethod = getCountMethod;
            consoleReflection.StartGettingEntriesMethod = startGettingEntriesMethod;
            consoleReflection.EndGettingEntriesMethod = endGettingEntriesMethod;
            consoleReflection.GetEntryInternalMethod = getEntryInternalMethod;
            consoleReflection.ConditionField = conditionField;
            consoleReflection.MessageField = messageField;
            consoleReflection.ModeField = modeField;
            return consoleReflection;
        }

        /// <summary>
        /// 优先读取 condition 字段，兼容旧版本回退到 message。
        /// </summary>
        private string GetLogMessage(ConsoleReflection consoleReflection, object entry)
        {
            var message = consoleReflection.ConditionField != null
                ? consoleReflection.ConditionField.GetValue(entry) as string
                : null;

            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }

            return consoleReflection.MessageField != null
                ? consoleReflection.MessageField.GetValue(entry) as string
                : null;
        }

        /// <summary>
        /// 按 CLI 约定把原始日志类型映射到 all / Log / Warning / Error 三类过滤。
        /// </summary>
        private bool ShouldIncludeLog(string logTypeFilter, string entryType)
        {
            if (string.IsNullOrEmpty(logTypeFilter) || string.Equals(logTypeFilter, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(NormalizeLogType(entryType), logTypeFilter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 统一过滤类别，错误类保留原始 type 但归并到 Error。
        /// </summary>
        private string NormalizeLogType(string entryType)
        {
            if (string.Equals(entryType, "AssetImportWarning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "ScriptingWarning", StringComparison.OrdinalIgnoreCase))
            {
                return "Warning";
            }

            if (string.Equals(entryType, "Error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "Assert", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "Fatal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "AssetImportError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "ScriptingError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "ScriptCompileError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "StickyError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "ScriptingException", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "GraphCompileError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "ScriptingAssertion", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryType, "VisualScriptingError", StringComparison.OrdinalIgnoreCase))
            {
                return "Error";
            }

            return "Log";
        }

        /// <summary>
        /// 根据 Unity Console mode 位标记推导原始日志类型。
        /// </summary>
        private string GetLogType(int mode)
        {
            if ((mode & (1 << 22)) != 0) return "VisualScriptingError";
            if ((mode & (1 << 21)) != 0) return "ScriptingAssertion";
            if ((mode & (1 << 20)) != 0) return "GraphCompileError";
            if ((mode & (1 << 17)) != 0) return "ScriptingException";
            if ((mode & (1 << 13)) != 0) return "StickyError";
            if ((mode & (1 << 12)) != 0) return "ScriptCompileWarning";
            if ((mode & (1 << 11)) != 0) return "ScriptCompileError";
            if ((mode & (1 << 10)) != 0) return "ScriptingLog";
            if ((mode & (1 << 9)) != 0) return "ScriptingWarning";
            if ((mode & (1 << 8)) != 0) return "ScriptingError";
            if ((mode & (1 << 7)) != 0) return "AssetImportWarning";
            if ((mode & (1 << 6)) != 0) return "AssetImportError";
            if ((mode & (1 << 5)) != 0) return "DontPreprocessCondition";
            if ((mode & (1 << 0)) != 0) return "Error";
            if ((mode & (1 << 1)) != 0) return "Assert";
            if ((mode & (1 << 2)) != 0) return "Log";
            if ((mode & (1 << 3)) != 0) return "Fatal";

            return "Log";
        }

        [Serializable]
        private class LogEntry
        {
            public string message;
            public string type;
        }

        /// <summary>
        /// 缓存一次 Console 反射所需的类型、方法和字段句柄。
        /// </summary>
        private class ConsoleReflection
        {
            public Type LogEntryType;
            public MethodInfo GetCountMethod;
            public MethodInfo StartGettingEntriesMethod;
            public MethodInfo EndGettingEntriesMethod;
            public MethodInfo GetEntryInternalMethod;
            public FieldInfo ConditionField;
            public FieldInfo MessageField;
            public FieldInfo ModeField;
        }
    }
}
