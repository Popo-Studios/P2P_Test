using UnityEngine;
using System.Collections.Concurrent;

public class Logger : MonoBehaviour {
    public static Logger Instance {
        get
        {
            if (_instance == null)
            {
                GameObject obj = new GameObject("Logger");
                _instance = obj.AddComponent<Logger>();
                DontDestroyOnLoad(obj);
            }
            return _instance;
        }
    }
    private static Logger _instance = null;

    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly ConcurrentQueue<string> _errorQueue = new();

    public void Log(string message) {
        _logQueue.Enqueue(message);
    }

    public void LogError(string message) {
        _errorQueue.Enqueue(message);
    }

    void Update() {
        while (_logQueue.TryDequeue(out var msg)) {
            Debug.Log(msg);
        }

        while (_errorQueue.TryDequeue(out var msg)) {
            Debug.LogError(msg);
        }
    }
}