using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

    private static MainThreadDispatcher Instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        if (Instance == null)
        {
            var go = new GameObject("MainThreadDispatcher");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MainThreadDispatcher>();
        }
    }

    public static void Execute(Action action)
    {
        _executionQueue.Enqueue(action);
    }

    void Update()
    {
        while (_executionQueue.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

   public static Task<T> Execute<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>();

        Execute(() =>
        {
            try
            {
                T result = action();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }
}