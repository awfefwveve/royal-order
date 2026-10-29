using System;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace KnifeWar.Diagnostics
{
    /// <summary>
    /// 콘솔 내용과 컴파일 오류를 프로젝트의 Logs/claude_console.txt에 기록한다.
    /// 다른 KnifeWar 코드에 의존하지 않아서, 게임 코드가 컴파일 실패해도 이 기록은 동작한다.
    /// </summary>
    [InitializeOnLoad]
    static class ConsoleFileLogger
    {
        static readonly string LogPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "claude_console.txt"));
        static readonly object Lock = new object();

        static ConsoleFileLogger()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.compilationStarted -= OnCompileStarted;
            CompilationPipeline.compilationStarted += OnCompileStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
            Write("[domain] reload");
        }

        static void OnCompileStarted(object _) => Write("[compile] started");

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            int errors = 0;
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error) continue;
                errors++;
                Write($"[compile-error] {m.file}({m.line},{m.column}): {m.message}");
            }
            Write($"[compile] {Path.GetFileName(assembly)} errors={errors}");
        }

        static void OnPlayMode(PlayModeStateChange s) => Write($"[playmode] {s}");

        static void OnLog(string message, string stack, LogType type)
        {
            string tag = type == LogType.Log ? "log" : type.ToString().ToLowerInvariant();
            string text = $"[{tag}] {message}";
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                // 스택은 앞부분만
                var lines = (stack ?? "").Split('\n');
                for (int i = 0; i < Math.Min(4, lines.Length); i++)
                    if (!string.IsNullOrWhiteSpace(lines[i])) text += "\n    " + lines[i].Trim();
            }
            Write(text);
        }

        static void Write(string line)
        {
            try
            {
                lock (Lock)
                {
                    File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}\n");
                    var info = new FileInfo(LogPath);
                    if (info.Length > 2_000_000)
                    {
                        var keep = File.ReadAllText(LogPath);
                        File.WriteAllText(LogPath, keep.Substring(keep.Length - 500_000));
                    }
                }
            }
            catch
            {
                // 기록 실패는 무시
            }
        }

        [MenuItem("KnifeWar/콘솔 기록 비우기", priority = 100)]
        static void Clear()
        {
            lock (Lock) File.WriteAllText(LogPath, "");
        }
    }
}
