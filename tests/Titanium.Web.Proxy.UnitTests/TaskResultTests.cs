using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Covers <see cref="TaskResult" /> and <see cref="TaskResult{T}" /> IAsyncResult wrappers.
/// </summary>
[TestClass]
public class TaskResultTests
{
    [TestMethod]
    public void TaskResult_CompletedTask_ExposesAsyncResultMembers()
    {
        var state = new object();
        var task = Task.CompletedTask;
        var result = new TaskResult(task, state);

        Assert.AreSame(state, result.AsyncState);
        Assert.IsNotNull(result.AsyncWaitHandle);
        Assert.IsTrue(result.IsCompleted);
        Assert.AreEqual(((IAsyncResult)task).CompletedSynchronously, result.CompletedSynchronously);
        result.GetResult();
    }

    [TestMethod]
    public void TaskResult_FaultedTask_GetResultThrows()
    {
        var task = Task.FromException(new InvalidOperationException("boom"));
        var result = new TaskResult(task, null);

        Assert.ThrowsExactly<InvalidOperationException>(() => result.GetResult());
    }

    [TestMethod]
    public void TaskResultOfT_FromResult_ExposesResultAndAsyncState()
    {
        var state = "state-token";
        var task = Task.FromResult(42);
        var result = new TaskResult<int>(task, state);

        Assert.AreSame(state, result.AsyncState);
        Assert.IsNotNull(result.AsyncWaitHandle);
        Assert.IsTrue(result.IsCompleted);
        Assert.AreEqual(42, result.Result);
        Assert.AreEqual(((IAsyncResult)task).CompletedSynchronously, result.CompletedSynchronously);
    }

    [TestMethod]
    public void TaskResultOfT_FaultedTask_ResultThrows()
    {
        var task = Task.FromException<int>(new InvalidOperationException("fail"));
        var result = new TaskResult<int>(task, null);

        var ex = Assert.ThrowsExactly<AggregateException>(() => _ = result.Result);
        Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidOperationException));
    }
}
