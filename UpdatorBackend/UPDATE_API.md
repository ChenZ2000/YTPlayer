# 易听（YTPlayer）自动升级 API

该升级服务由 `update.php` 提供，部署路径为 `https://yt.chenz.cloud/update.php`。它通过 GitHub Releases 动态获取最新版本信息、缓存并回传编译好的二进制压缩包，并在整个流程中提供状态标志与百分比进度。

## 接口汇总

| 功能 | Method | Path | 说明 |
| --- | --- | --- | --- |
| 检查更新 | `GET` | `/update.php?action=check&version={currentVersion}` | 返回最新版本、可用资产列表与进度数据 |
| 下载升级包 | `GET` | `/update.php?action=download&asset={assetName}` | streams 最新压缩包（二进制），若 `asset` 省略则按优先级自动选择 |

> **提示**：`version` 与 `asset` 参数也可通过 `POST`、`application/x-www-form-urlencoded` 方式提交，服务端统一读取 `$_REQUEST`。

## 通用响应与进度字段

无论是 JSON 还是二进制响应，服务都会附带以下结构化进度信息：

- **Header** `X-YT-Update-Progress`: JSON 数组，描述各阶段 `step/state/progress/message`。
- JSON 响应（检查更新或错误时）会在 `progress` 字段中重复同样的数据，并包含 `timestamp`。

示例进度（JSON）：

```json
[
  {"step":"fetch_release","state":"completed","progress":70,"message":"Release metadata retrieved"},
  {"step":"compute","state":"completed","progress":100,"message":"Version comparison complete"}
]
```

客户端可轮询或显示这些阶段来反馈 UI 状态。

## 检查更新 (`action=check`)

```
GET https://yt.chenz.cloud/update.php?action=check&version=1.2.3
```

响应示例：

```json
{
  "status": "ok",
  "timestamp": "2025-11-10T04:25:12Z",
  "progress": [
    {"step":"fetch_release","state":"completed","progress":70,"message":"Release metadata retrieved"},
    {"step":"compute","state":"completed","progress":100,"message":"Version comparison complete"}
  ],
  "data": {
    "clientVersion": "1.2.3",
    "latest": {
      "tag": "v1.4.0",
      "semantic": "1.4.0",
      "name": "YTPlayer 1.4.0",
      "publishedAt": "2025-10-02T15:31:11Z",
      "notes": "发布说明 Markdown 原文……",
      "releasePage": "https://github.com/ChenZ2000/YTPlayer/releases/tag/v1.4.0"
    },
    "updateAvailable": true,
    "assets": [
      {
        "name": "YTPlayer-win-x64.zip",
        "size": 45213876,
        "contentType": "application/zip",
        "updatedAt": "2025-10-02T15:33:00Z",
        "download": "https://yt.chenz.cloud/update.php?action=download&asset=YTPlayer-win-x64.zip"
      }
    ]
  }
}
```

客户端只需比较 `updateAvailable` 字段；如需更细粒度控制，可自定义版本比较规则或过滤资产列表。

## 下载更新包 (`action=download`)

```
GET https://yt.chenz.cloud/update.php?action=download&asset=YTPlayer-win-x64.zip
```

- `asset` 可填完整文件名或其中一部分（大小写不敏感）。省略时服务端会按扩展名优先级（zip → 7z → tar.gz → …）自动选择。
- 响应头：
  - `Content-Type: application/octet-stream`
  - `Content-Disposition: attachment; filename="YTPlayer-win-x64.zip"; filename*=UTF-8''YTPlayer-win-x64.zip`
  - `X-YT-Update-Progress: [...]`
- 响应体：实际压缩包内容；服务端会先检查 `cache/` 目录是否已有匹配版本，命中即直接流式回传，否则实时拉取 GitHub 文件、更新缓存再回传。

若下载过程中出现错误（GitHub 不可达、磁盘不可写等），服务端会返回 JSON 错误包（HTTP 5xx）并附带进度，使客户端可感知失败阶段。

## 缓存与并发策略

- 缓存目录：`update.php` 同级的 `cache/`，包含 `latest.bin` 与 `metadata.json`。
- 元数据字段：`asset_id/asset_name/size/version/tag/downloaded_at` 用于验证缓存是否仍代表最新 Release。
- 通过文件锁（`.lock`）保证同一时刻仅有一次真实下载，其余请求会等待缓存更新或直接复用已缓存文件，适合高并发访问。
- 下载完成后才会替换缓存文件，避免返回半成品；同时利用流式输出，不会占用过多 PHP 内存。

## 客户端集成建议

1. **检查更新**：启动时调用 `action=check`；根据 `updateAvailable` 决定是否提示升级。
2. **选择资产**：使用返回的 `assets[].download` 字段，或解析 `name` 确定平台后自行构建查询参数。
3. **展示进度**：
   - 解析 JSON 响应的 `progress`。
   - 下载阶段可读取响应头 `X-YT-Update-Progress`（为 JSON 字符串），实时呈现当前步骤；由于下载为流式传输，服务端会在响应开始时返回最终进度列表。
4. **安全**：可在客户端附带自定义 Header（如应用签名、渠道编号），未来若需要做配额或鉴权，可在 `update.php` 内扩展检查逻辑。
5. **超时重试**：GitHub API 失败时将返回 5xx JSON 错误，客户端可指数退避重试；下载操作若返回 HTTP 4xx/5xx，同样可根据 `progress` 中的 `step` 判断是元数据、缓存还是下载阶段失败。

## 常见问题

| 问题 | 排查方法 |
| --- | --- |
| 频繁命中旧版本 | 确认 GitHub Releases 是否真正发布新 tag；服务端使用 `releases/latest`，不会读取 Draft/Pre-release |
| `asset` 找不到 | 参数大小写不敏感，但需要包含在最新 Release 的资产名称中；可先调用 `action=check` 获取完整列表 |
| GitHub 访问受限 | 服务端主动抓取并缓存，客户端无需直连 GitHub；若服务器自身无法访问 GitHub，请配置代理或镜像 |
| 大文件下载中断 | 下载采用流式输出，可在客户端开启断点续传或重试逻辑；服务端会在缓存完成后再开始输出，避免损坏文件 |

## 未来扩展

- 可在 `CacheStore` 中加上多资产并存策略（按资产名称区分），便于同时维护多平台包。
- 可扩展额外参数（如 `channel=beta`）映射到 GitHub Pre-release 或特定 tag。
- 若需强校验，可在 JSON 响应中增加 `sha256` 字段（GitHub API 已提供 `browser_download_url`，可额外拉取 `checksum` 文件或自行计算后缓存）。

如需调整或新增动作，只需在 `update.php` 中扩展 `action` 分支并复用现有 `ReleaseClient`、`CacheStore`、`ProgressTracker` 等组件即可。

