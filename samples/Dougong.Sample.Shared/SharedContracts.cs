namespace Dougong.Sample.Shared;

/// <summary>宿主与插件共同引用的契约程序集：类型同一性必须跨 ALC 保持。</summary>
public interface IGreetingService
{
    string Greet(string name);
}

/// <summary>跨 ALC 的事件载荷，同样来自共享契约程序集。</summary>
public sealed record GreetingEvent(string Message);

/// <summary>
/// 共享契约程序集内的探针：宿主与插件拿到的是同一静态实例，
/// 用来观察插件 ALC 中发布的事件是否以同一类型抵达宿主。
/// </summary>
public static class GreetingProbe
{
    private static TaskCompletionSource<GreetingEvent> _received = NewSource();

    public static Task<GreetingEvent> Received => Volatile.Read(ref _received).Task;

    public static void Reset() => Interlocked.Exchange(ref _received, NewSource());

    public static void Report(GreetingEvent evt) => Volatile.Read(ref _received).TrySetResult(evt);

    private static TaskCompletionSource<GreetingEvent> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
