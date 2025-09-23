using System.Collections;
using UnityEngine;

public class CoroutineRunner : MonoBehaviour
{
    public static CoroutineRunner Instance {
        get
        {
            if (_instance == null)
            {
                GameObject obj = new GameObject("CoroutineRunner");
                _instance = obj.AddComponent<CoroutineRunner>();
                DontDestroyOnLoad(obj);
            }
            return _instance;
        }
    }
    private static CoroutineRunner _instance = null;

    public void Run(IEnumerator coroutine)
    {
        StartCoroutine(coroutine);
    }
}
