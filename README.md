<p align="center">
  <img src="assets/icon.png" width="128" height="128" alt="飞牛实时同步 Logo">
</p>

<h1 align="center">飞牛实时同步</h1>

<p align="center">飞牛影音与 Jellyfin 的收藏、已观看和播放进度双向同步插件。</p>

<p align="center">
  <a href="https://github.com/sanfuhualv/jellyfin-plugin-fnos-sync/releases">下载</a> ·
  <a href="LICENSE">许可证</a>
</p>

## 功能

- Jellyfin 状态修改后自动推送至飞牛影视，查看媒体时按需拉去飞牛影视状态。
- 支持轻量检查、每日完整核对和冲突策略。

当前版本 **1.0.0**，适用于 **Jellyfin 12.4 / .NET 10**。页面自动刷新适配 Jellyfin Web，其他客户端效果可能不同。

## 安装

在 Jellyfin「控制台 → 插件 → 仓库」添加以下地址，安装后重启：

```text
https://github.com/sanfuhualv/jellyfin-plugin-fnos-sync/releases/latest/download/manifest.json
```

手动安装可从 Releases 下载插件 ZIP，停止 Jellyfin 后解压到配置目录的 `plugins/FnosSync_1.0.0/`，再启动。

## 使用

1. 打开插件设置，填写飞牛地址、账号和密码，选择 Jellyfin 用户，保存并测试连接。
2. 点击「发现映射」，检查并保存，再启用同步。
3. 在「控制台 → 计划任务 → 飞牛媒体库同步」设置检查时间。

默认首次只建立基线，双方冲突保留待处理。请避免其他同步器同时写入相同媒体状态。

迁移或备份时，请同时保留 Jellyfin 数据目录中 `fnos-sync/credentials/` 的密文与密钥文件；不要公开上传这些运行文件。

---

第三方项目，与 Jellyfin、飞牛官方无隶属关系。采用 [GPL-3.0](LICENSE)，第三方说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，本版本源码见 [v1.0.0](https://github.com/sanfuhualv/jellyfin-plugin-fnos-sync/tree/v1.0.0)。
