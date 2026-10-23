namespace Dougong.Kernel;

/// <summary>解析已提交服务的代次引用。缺失、歧义或所属作用域关闭时返回 null。</summary>
public interface IResolver
{
    IServiceReference<TService>? Resolve<TService>(string? name = null) where TService : class;
}

/// <summary>
/// 绑定提供方激活代次的服务引用。每次调用取得短期租约，停用后不能再调用。
/// 委托必须等待全部工作结束，不得将实例保存到调用外或启动未等待的后台工作。
/// </summary>
public interface IServiceReference<TService> where TService : class
{
    ValueTask InvokeAsync(
        Func<TService, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);

    ValueTask<TResult> InvokeAsync<TResult>(
        Func<TService, CancellationToken, ValueTask<TResult>> action,
        CancellationToken cancellationToken = default);
}
