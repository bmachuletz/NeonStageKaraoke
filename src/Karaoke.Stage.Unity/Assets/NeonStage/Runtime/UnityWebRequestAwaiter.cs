using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace NeonStage.Stage
{

internal static class UnityWebRequestAwaiter
{
    public static TaskAwaiter<UnityWebRequest> GetAwaiter(this UnityWebRequestAsyncOperation operation)
    {
        var completion = new TaskCompletionSource<UnityWebRequest>();
        if (operation.isDone)
            completion.TrySetResult(operation.webRequest);
        else
            operation.completed += _ => completion.TrySetResult(operation.webRequest);
        return completion.Task.GetAwaiter();
    }
}
}
