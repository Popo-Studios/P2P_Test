using UnityEngine;
using System.Collections.Concurrent;

public class Logger : MonoBehaviour {
    private static readonly ConcurrentQueue<string> _logQueue = new();
    private static readonly ConcurrentQueue<string> _errorQueue = new();

    public static void Log(string message) {
        _logQueue.Enqueue(message);
    }

    public static void LogError(string message) {
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