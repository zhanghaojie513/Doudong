namespace Dougong.Sample.Isolated;

/// <summary>
/// 仅供“漏配共享契约”回归测试使用的契约程序集。
/// 测试项目刻意不引用本程序集，因此默认 ALC 中不存在它；
/// 只有宿主显式登记后，插件才能解析到宿主的同一实例。
/// </summary>
public interface IIsolatedProbe
{
    string Describe();
}
