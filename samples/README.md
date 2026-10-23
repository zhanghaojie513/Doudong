# samples

每个里程碑一个可运行样例。样例是插件作者文档的活参照：API 变更时先改样例，再改文档。

| 样例 | 里程碑 | 内容 |
|---|---|---|
| `Dougong.Sample.Plugin/` | M0 | 单插件服务注册与清理，用于动态加载和回收验证 |
| `Dougong.Sample.Contracts/` | M0 | 契约版本、失败清理、共享契约插件 |
| `Dougong.Sample.Shared/` | M0 | 宿主与插件共同引用的共享契约程序集（服务接口与事件 DTO） |
| `m1-minimal-agent/` | M1 | headless 跑通一个真实编码任务（规划） |
| `m2-acp-mcp/` | M2 | ACP 客户端驱动 + MCP server 接入（规划） |

动态插件样例使用 `EnableDynamicLoading`，并把 `Dougong.Kernel` 标记为不复制到输出，
以便加载路径真实走 collectible AssemblyLoadContext。

共享契约样例用于验证跨 ALC 类型同一性：宿主调用 `PluginLoadContext.RegisterSharedContract`
显式登记契约程序集，插件程序集则不复制该契约。
