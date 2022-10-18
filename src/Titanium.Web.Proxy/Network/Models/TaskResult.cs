using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     Mimic a Task but you can set AsyncState
/// </summary>
public class TaskResult : IAsyncResult
{
    private readonly Task task;

    public TaskResult(Task pTask, object state)
    {
        task = pTask;
        AsyncState = state;
    }

    public object AsyncState { get; }

    public WaitHandle AsyncWaitHandle => ((IAsyncResult)task).AsyncWaitHandle;

    public bool CompletedSynchronously => ((IAsyncResult)task).CompletedSynchronously;

    public bool IsCompleted => task.IsCompleted;

    public void GetResult()
    {
        task.GetAwaiter().GetResult();
    }
}

/// <summary>
///     Mimic a Task&lt;T&gt; but you can set AsyncState
/// </summary>
/// <typeparam name="T"></typeparam>
public class TaskResult<T> : IAsyncResult
{
    private readonly Task<T> task;

    public TaskResult(Task<T> pTask, object state)
    {
        task = pTask;
        AsyncState = state;
    }

    public T Result => task.Result;

    public object AsyncState { get; }

    public WaitHandle AsyncWaitHandle => ((IAsyncResult)task).AsyncWaitHandle;

    public bool CompletedSynchronously => ((IAsyncResult)task).CompletedSynchronously;

    public bool IsCompleted => task.IsCompleted;
}