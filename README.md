# SGS 资源收集器

三国杀 H5 资源的版本化归档工具，由两部分组成：

1. **Collector**（`collector/`，.NET 10 / ASP.NET Core）：常驻程序，定时或手动从官方站点发现并下载资源，解包、解密 `.sgs`，在本地 SQLite 记录资源、版本和运行记录，并提供只监听本机的 Web 管理界面。
2. **Worker**（`worker/`，TypeScript / Cloudflare Workers）：R2 保存内容（按 xxHash128 内容寻址），Durable Object（SQLite）保存版本目录，对外提供资源与查询接口。

数据流：

```
官方站点 --条件GET--> Collector --PUT /internal/blobs/{hash}--> Worker --> R2
                       |  本地 SQLite                  POST /internal/sync --> Worker --> Durable Object
公网用户 --> /resource/... /api/... --> Worker --> 边缘缓存 / Durable Object / R2
```

历史版本永不删除；资源从站点消失后旧版本仍可访问。

## 目录结构

```
collector/
  Sgs.Collector.slnx  Dockerfile
  src/Sgs.Collector/     收集程序（appsettings.json、Options、Data、Http、Discovery、Decode、Crawl、Sync ...）
  tests/Sgs.Collector.Tests/
worker/
  src/                   index.ts catalog.ts resource.ts api.ts internal.ts http.ts
  test/                  vitest 测试
  wrangler.jsonc  package.json  .dev.vars.example
.github/workflows/       ci.yml（构建与测试）  release.yml（打 tag 发布）
```

## 环境要求

- .NET 10 SDK
- Node.js 22（含 npm）
- 部署时需要 Cloudflare 账号（Workers + R2）

## 本地开发与联调

### 1. 启动 Worker

```bash
cd worker
npm ci
cp .dev.vars.example .dev.vars     # 内容为 SYNC_TOKEN=dev-token
npm test
npm run typecheck
npx wrangler dev                   # 监听 http://127.0.0.1:8787，使用本地 R2 与 Durable Object
```

### 2. 构建 Collector

```bash
dotnet build collector/Sgs.Collector.slnx
dotnet test collector/Sgs.Collector.slnx
```

### 3. 本机配置

新建 `collector/src/Sgs.Collector/appsettings.Local.json`（已被 gitignore）。下面是联调用的小规模配置（只处理 300 条，不自动调度）：

```json
{
  "Collector": { "MaxItems": 300, "IntervalMinutes": 0 },
  "Worker": { "BaseUrl": "http://127.0.0.1:8787", "SyncToken": "dev-token" }
}
```

正式运行时去掉 `MaxItems`（或设为 0），`BaseUrl` / `SyncToken` 换成线上值。

### 4. 启动 Collector

```bash
dotnet run --project collector/src/Sgs.Collector
```

然后打开 http://127.0.0.1:5080 。注意：

- 数据库里还没有任何收集记录时，**不会自动开始**，第一轮必须在管理界面点“开始收集”手动启动；之后按 `IntervalMinutes` 自动调度。
- 首个完整轮约下载 1-2 GB、1.2-1.5 万个资源。

## 配置项说明

配置来源及优先级（后者覆盖前者）：

`appsettings.json` < `appsettings.{Environment}.json` < `appsettings.Local.json` < 环境变量（如 `Worker__SyncToken`） < 命令行

配置不合法时程序启动失败，并给出中文错误提示。

### Collector

| 配置项 | 默认值 | 范围 / 规则 | 说明 |
|---|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | - | 管理界面监听地址 |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | - | 允许的 Host，防 DNS 重绑定 |
| `Collector:DataDir` | `App_Data` | 相对路径按程序内容根目录解析 | 数据目录，数据库为 `App_Data/collector.db` |
| `Collector:EntryUrl` | `https://web.sanguosha.com/220/h5_2/index_210000.php` | 绝对 http/https 地址 | 入口页 |
| `Collector:MaxConcurrency` | 16 | 1-64 | 并发下载数 |
| `Collector:TimeoutSeconds` | 60 | 5-600 | 单次请求超时 |
| `Collector:RetryCount` | 2 | 0-5 | 失败重试次数 |
| `Collector:MaxResponseMegabytes` | 95 | 1-95 | 单个响应体大小上限（受 Worker 请求体 100 MB 限制） |
| `Collector:IntervalMinutes` | 360 | >= 0 | 自动收集间隔（从上一轮结束时算起），0 表示仅手动 |
| `Collector:FailureRetentionDays` | 30 | >= 1 | 失败记录保留天数 |
| `Collector:MaxItems` | 0 | >= 0 | 调试用：大于 0 时每轮最多处理这么多条 |

### Worker 连接（Collector 侧）

| 配置项 | 默认值 | 范围 / 规则 | 说明 |
|---|---|---|---|
| `Worker:BaseUrl` | 空 | 必填，绝对 http/https 地址 | Worker 地址 |
| `Worker:SyncToken` | 空 | 必填，非空 | 与 Worker 的 `SYNC_TOKEN` 相同 |
| `Worker:TimeoutSeconds` | 300 | 10-3600 | 上传 / 同步请求超时 |
| `Worker:SyncBatchSize` | 500 | 1-1000 | 每批同步的版本数 |

### Worker 自身

| 名称 | 说明 |
|---|---|
| `SYNC_TOKEN`（secret / `.dev.vars`） | 保护 `/internal/*` 的 Bearer 密钥；为空时一律拒绝 |
| R2 绑定 `BLOBS` | 桶名 `sgs-resources` |
| Durable Object 绑定 `CATALOG` | 类 `Catalog`（SQLite） |

## Worker 部署

在 `worker/` 目录下执行：

1. 登录并创建 R2 桶：
   ```bash
   npx wrangler login
   npx wrangler r2 bucket create sgs-resources
   ```
2. 设置同步密钥（建议 32 位以上随机串）：
   ```bash
   npx wrangler secret put SYNC_TOKEN
   ```
3. 在 `wrangler.jsonc` 中加入自定义域名路由，然后部署。边缘缓存只在自定义域名上生效（`*.workers.dev` 上无效）：
   ```jsonc
   "routes": [{ "pattern": "<你的域名>", "custom_domain": true }]
   ```
   ```bash
   npx wrangler deploy
   ```
4. 若该域名开启了 Bot Fight Mode / WAF 挑战，需要为 `/internal/*` 添加放行规则，否则 Collector 的上传会被拦截。
5. 在 Collector 的 `appsettings.Local.json`（或环境变量）中设置 `Worker:BaseUrl = https://<你的域名>`、`Worker:SyncToken = <同一个密钥>`。

## 对外接口

对外接口不需要验证，均支持 CORS。key 为 `host[:非默认端口]` + 解码后的路径，不含协议和查询参数，区分大小写，例如 `web.sanguosha.com/220/h5_2/res/config/Config_w.sgs`。包内成员的 key 是 `包 key/成员路径`。

| 接口 | 说明 |
|---|---|
| `GET /resource/{版本}/{key}` | 指定版本的内容，如 `/resource/3/web.sanguosha.com/...` |
| `GET /resource/{key}` | 最新版内容 |
| `GET /api/list` | 全部资源及其版本列表（JSON） |
| `GET /api/info/{key}` | 单个资源的最新版本号与全部版本信息（JSON） |

要点：

- `/resource/*` 同时支持 `HEAD`。版本号是不含前导 0 的正整数，且后面还有路径时才被识别为版本。
- 缓存：带版本号的地址 `Cache-Control: public, max-age=31536000, immutable`；最新版地址、`/api/list`、`/api/info` 为 `max-age=60`，即同步后最多 60 秒才能看到新版本。错误响应一律 `no-store`。
- `ETag` 为内容哈希（`"xxh128"`），支持 `If-None-Match`（含弱校验）返回 304；响应头 `X-Resource-Version` 为版本号。
- 支持单段 `Range`（`bytes=a-b`、`a-`、`-n`），返回 206；越界返回 416；多段 Range 被忽略并返回完整内容。
- 响应带 `X-Content-Type-Options: nosniff` 和 `Content-Security-Policy: sandbox`。
- 根据 Cloudflare 免费版额度，每个对外请求（含缓存命中）都会计入 Worker 请求数。

## 管理界面与防护

- 地址：http://127.0.0.1:5080 ，无登录，只监听本机回环地址。
- 四个标签页：进度（状态、开始 / 终止、立即同步、全量重新同步、从 Worker 恢复）、资源（搜索、筛选、详情）、收集记录、Header 规则（含“测试”区）。Header 规则的修改在下一轮收集生效。
- 防护：
  - `AllowedHosts` 限制 Host，防 DNS 重绑定；如需局域网访问，必须同时修改 `Urls` 和 `AllowedHosts`。
  - 所有修改类（非 GET）`/api` 请求必须带请求头 `X-Requested-With: sgs-collector`，并使用 `Content-Type: application/json`（无请求体时发送 `{}`），否则返回 403。例如：
    ```bash
    curl -X POST http://127.0.0.1:5080/api/runs -H "X-Requested-With: sgs-collector" -H "Content-Type: application/json" -d "{}"
    ```

## 本地数据库丢失后的恢复

每轮收集开始前，Collector 会比较本地与 Worker 的版本数；本地更少时拒绝收集并提示恢复，避免新版本号与已发布的版本冲突。恢复步骤：

1. 停止 Collector。
2. 删除数据库文件：
   ```bash
   rm collector/src/Sgs.Collector/App_Data/collector.db*
   ```
   （发布包中则是程序目录下的 `App_Data/collector.db*`；Docker 中见下文“Docker 运行 Collector”。）
3. 启动 Collector。
4. 在管理界面点“从 Worker 恢复”（仅在本地没有收集产生的版本时可用）。
5. 恢复完成后再点“开始收集”。恢复后的首轮会完整下载一遍，大部分条目显示“内容未变”。

不会被恢复的内容：Header 规则（恢复为默认规则）、收集记录、失败记录，以及资源的 ETag、Last-Modified 和发现的链接（首轮重新建立）。

## 发布

推送形如 `v*` 的 tag 会触发 `.github/workflows/release.yml`：测试通过后构建并创建 GitHub Release，附带以下文件（`<tag>` 为 tag 名）：

- `sgs-collector-<tag>-win-x64.zip`
- `sgs-collector-<tag>-linux-x64.zip`（以上两个均为 self-contained，无需安装 .NET）
- `sgs-worker-<tag>.zip`（Worker 源码、`wrangler.jsonc` 与 dry-run 打包产物 `dist`）

同时把 Collector 的 Docker 镜像（linux/amd64、linux/arm64）推送到 `ghcr.io/suujonh/sgs-resource-collector`，标签为 `<版本>`（如 `1.0.0`）、`<主版本>.<次版本>`、`<tag>` 和 `latest`。

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Docker 运行 Collector

```bash
docker run -d --name sgs-collector --restart unless-stopped --stop-timeout 30 \
  -p 127.0.0.1:5080:5080 \
  -v sgs-collector-data:/data \
  -e Worker__BaseUrl=https://<你的域名> \
  -e Worker__SyncToken=<同步密钥> \
  ghcr.io/suujonh/sgs-resource-collector:latest
```

- 管理界面仍为 http://127.0.0.1:5080 。容器内监听 `0.0.0.0:5080`，所以端口映射务必写成 `127.0.0.1:5080:5080`，不要暴露到局域网或公网（管理界面无登录）。用其它主机名访问时，还需设置 `-e AllowedHosts=<主机名>`。
- 数据目录为容器内的 `/data`（数据库 `/data/collector.db`），请挂载卷保存。容器以非 root 用户（UID 1654）运行，挂载宿主机目录时需保证该用户可写。
- 其它配置项同样用环境变量设置，`:` 写成 `__`，如 `-e Collector__IntervalMinutes=720`；也可把 `appsettings.Local.json` 挂载到 `/app/appsettings.Local.json`。
- `--stop-timeout 30`：停止容器时程序会终止当前轮次并写完数据库，留出足够时间，避免被强制结束。
- 本地数据库丢失后的恢复：停止容器，删除卷中的 `collector.db*`（或换一个新卷）后启动，再按上文步骤恢复。
- 本地构建镜像：`docker build -t sgs-collector collector`。

## 注意事项

- **免费版额度**：Workers 免费版每天 10 万次请求，且包含边缘缓存命中；首轮约 1.3 万次上传也占用额度。对外流量大时建议使用 Workers Paid。
- **最新版有最多 60 秒延迟**，带版本号的地址不受影响。
- **单一 Durable Object**：所有目录查询经过同一个 DO，配合边缘缓存对资料站足够。
- **存储增长**：历史永不删除，R2 免费的 10 GB 预计可用数年。
- **版本号以本地数据库为准**：Worker 侧 (key, 版本) 到哈希一经写入不再改变。同步报告“冲突”说明本地数据库与 Worker 不一致（例如使用了旧备份），应停止收集，删除本地数据库，再执行“从 Worker 恢复”。
- **成员 key 一旦确定就不再改变**：若首次推断的扩展名不理想，需手动修改数据库，并在 Worker 侧另行处理。
- 被终止或出错的轮次不会同步，待下次正常完成时一并推送，也可在界面手动同步；“全量重新同步”只重推，不会清空 Worker。
