using KSP;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace G3MagnetBoots
{
    internal static class Logger
    {
        internal static string ModPrefix = "[" + Assembly.GetExecutingAssembly().GetName().Name + "]";
        internal static bool IsDebugMode;

        private static readonly string LogFilePath =
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData/G3MagnetBoots/Debug/G3MagnetBoots.log").Replace("\\", "/");

        private static readonly StreamWriter _fileWriter;
        private static readonly object _fileLock = new();

        private enum Level { Info, Debug, Warn, Error, Exception }

        static Logger()
        {
            IsDebugMode = true;
            try
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                _fileWriter = new StreamWriter(new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                WriteToFileRaw("--                                                              ---");
                WriteToFileRaw($"--- Logger initialized: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ---");
                WriteToFileRaw($"KSP root: {KSPUtil.ApplicationRootPath} -> {LogFilePath}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"{ModPrefix} Logger file init failed: {ex}");
                _fileWriter = null;
            }
        }

        // ---- Public API ----

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Debug(string message = "", string detail = "")
        {   
            if (!IsDebugMode) return;
            LogCore(Level.Debug, message, detail, includeCaller: true);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Trace(string message = "")
        {
            if (!IsDebugMode) return;
            LogCore(Level.Debug, message, detail: "", includeCaller: true);
        }

        internal static void Info(string message, string detail = "")
            => LogCore(Level.Info, message, detail, includeCaller: false);

        internal static void Warning(string message, string detail = "")
            => LogCore(Level.Warn, message, detail, includeCaller: false);

        internal static void Error(string message, string detail = "")
            => LogCore(Level.Error, message, detail, includeCaller: false);

        internal static void Exception(Exception ex)
        {
            if (!IsDebugMode) return;
            UnityEngine.Debug.LogException(ex);
            LogCore(Level.Exception, $"{ex.GetType().Name}: {ex.Message}", ex.StackTrace ?? "", includeCaller: false);
        }

        // ---- Core ----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LogCore(Level level, string message, string detail, bool includeCaller)
        {
            if (!IsDebugMode) return;

            string tag = "";
            if (includeCaller)
            {
                // 2 frames up: LogCore <- Debug/Trace <- caller
                var m = new StackFrame(2, false).GetMethod();
                string cls = m?.DeclaringType?.Name ?? "UnknownClass";
                string mem = m?.Name ?? "UnknownMethod";
                tag = $"[{cls}.{mem}] ";
            }

            string lvl = level switch
            {
                Level.Debug => "[DEBUG] ",
                Level.Warn => "[WARN] ",
                Level.Error => "[ERROR] ",
                Level.Exception => "[EXCEPTION] ",
                _ => ""
            };

            string outMsg = $"{ModPrefix} {lvl}{tag}{message}".TrimEnd();

            if (!string.IsNullOrEmpty(detail))
                outMsg += " " + detail;

            // Unity sink
            switch (level)
            {
                case Level.Warn: UnityEngine.Debug.LogWarning(outMsg); break;
                case Level.Error: UnityEngine.Debug.LogError(outMsg); break;
                case Level.Exception: UnityEngine.Debug.LogError(outMsg); break;
                default: UnityEngine.Debug.Log(outMsg); break;
            }

            WriteToFile(outMsg);
        }

        private static void WriteToFile(string message)
        {
            try
            {
                if (_fileWriter == null) return;
                lock (_fileLock)
                {
                    _fileWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"{ModPrefix} Logger file write failed: {ex}");
            }
        }

        // For early startup messages where you don't want double prefixes/tags.
        private static void WriteToFileRaw(string message)
        {
            try
            {
                if (_fileWriter == null) return;
                lock (_fileLock)
                {
                    _fileWriter.WriteLine(message);
                }
            }
            catch { }
        }
    }
}
