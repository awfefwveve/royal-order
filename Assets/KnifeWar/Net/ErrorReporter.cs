using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 플레이어 PC에서 난 오류(예외·Error 로그)를 모아 백엔드 /client/report 로 보낸다.
    /// - 같은 오류는 한 번만 (횟수만 셈), 1분에 한 번 묶어서 전송
    /// - 보내기 전에 파일(errors_pending.json)에도 남겨서, 게임이 통째로 꺼져도 다음 실행 때 보냄
    /// - 에디터·데디 서버에서는 안 보냄 (서버는 자체 로그 파일이 있음)
    /// </summary>
    public sealed class ErrorReporter : MonoBehaviour
    {
        [Serializable] sealed class Entry { public string type; public string msg; public string stack; public int count; }
        [Serializable] sealed class Report { public string version; public string os; public string gpu; public string cpu; public int ramMb; public string scene; public Entry[] items; }

        const int MaxEntries = 20;
        const float SendEvery = 60f;
        static ErrorReporter _inst;
        static string BackendUrl = "";

        readonly Dictionary<string, Entry> _pending = new Dictionary<string, Entry>();
        readonly object _lock = new object();
        float _lastSend = -999f;
        bool _dirty;

        static string PendingPath => Path.Combine(Application.persistentDataPath, "errors_pending.json");

        public static void Install(string backendUrl)
        {
            BackendUrl = (backendUrl ?? "").Trim();
            if (_inst != null || Application.isEditor || Application.isBatchMode) return;
            var go = new GameObject("ErrorReporter");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<ErrorReporter>();
        }

        public static void SetBackend(string backendUrl) => BackendUrl = (backendUrl ?? "").Trim();

        void Awake()
        {
            Application.logMessageReceivedThreaded += OnLog;
            LoadPending();
        }

        void OnDestroy() => Application.logMessageReceivedThreaded -= OnLog;

        void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            if (msg != null && msg.Length > 600) msg = msg.Substring(0, 600);
            if (stack != null && stack.Length > 2000) stack = stack.Substring(0, 2000);
            string key = type + "|" + msg + "|" + (stack ?? "").Split('\n')[0];
            lock (_lock)
            {
                if (_pending.TryGetValue(key, out var e)) { e.count++; }
                else if (_pending.Count < MaxEntries) _pending[key] = new Entry { type = type.ToString(), msg = msg, stack = stack, count = 1 };
                _dirty = true;
            }
        }

        void Update()
        {
            if (!_dirty || Time.unscaledTime - _lastSend < SendEvery || string.IsNullOrEmpty(BackendUrl)) return;
            _lastSend = Time.unscaledTime;
            SavePending();
            StartCoroutine(Send());
        }

        void OnApplicationQuit() { if (_dirty) SavePending(); }

        IEnumerator Send()
        {
            Entry[] items;
            lock (_lock)
            {
                items = new Entry[_pending.Count];
                _pending.Values.CopyTo(items, 0);
            }
            if (items.Length == 0) yield break;
            var r = new Report
            {
                version = GameUpdater.ReadLocalVersion(),
                os = SystemInfo.operatingSystem,
                gpu = SystemInfo.graphicsDeviceName,
                cpu = SystemInfo.processorType,
                ramMb = SystemInfo.systemMemorySize,
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                items = items,
            };
            bool ok = false;
            yield return new BackendClient(BackendUrl).Post("/client/report", JsonUtility.ToJson(r), (code, _) => ok = code == 200);
            if (!ok) yield break;   // 실패하면 파일에 남아 있다가 다음에 다시
            lock (_lock)
            {
                foreach (var e in items)
                {
                    string key = e.type + "|" + e.msg + "|" + (e.stack ?? "").Split('\n')[0];
                    if (_pending.TryGetValue(key, out var cur) && cur.count <= e.count) _pending.Remove(key);
                }
                _dirty = _pending.Count > 0;
            }
            SavePending();
        }

        void SavePending()
        {
            try
            {
                Entry[] items;
                lock (_lock) { items = new Entry[_pending.Count]; _pending.Values.CopyTo(items, 0); }
                if (items.Length == 0) { if (File.Exists(PendingPath)) File.Delete(PendingPath); return; }
                File.WriteAllText(PendingPath, JsonUtility.ToJson(new Report { items = items }), Encoding.UTF8);
            }
            catch { }
        }

        void LoadPending()
        {
            try
            {
                if (!File.Exists(PendingPath)) return;
                var r = JsonUtility.FromJson<Report>(File.ReadAllText(PendingPath));
                if (r?.items == null) return;
                lock (_lock)
                    foreach (var e in r.items)
                        _pending[e.type + "|" + e.msg + "|" + (e.stack ?? "").Split('\n')[0]] = e;
                _dirty = _pending.Count > 0;
                _lastSend = -SendEvery + 10f;   // 켜고 10초 뒤 전송
            }
            catch { }
        }
    }
}
