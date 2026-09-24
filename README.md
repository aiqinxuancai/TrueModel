<div align="center">

# TrueModel

**纯 C# 的模型检测与归因管理平台**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![SQLite](https://img.shields.io/badge/SQLite-本地持久化-003B57)
![Docker](https://img.shields.io/badge/Docker-支持-2496ED)

多站点 · 多 Key · 定时检测 · 指纹归因 · 结果通知

</div>

## 项目来源

本项目的模型归因方法、挑战提示词和默认指纹库源自 **[xqy2006/ModelTrace](https://github.com/xqy2006/ModelTrace)**，感谢原作者的研究与开源贡献。

参考版本：[60949ef](https://github.com/xqy2006/ModelTrace/tree/60949ef522a84f66b1236b459308b48028d36949)。TrueModel 将检测流程移植为 C#，**运行和发布均不依赖 Python**。移植部分与指纹库保留上游 MIT 许可，详见 [来源与许可](THIRD-PARTY-NOTICES.md)。

## 功能

内置默认指纹库为 ModelTrace 提交 `55a2e4a55170423b484d701e9a82ab62b268c811`，包含 16 个模型。启动不会自动升级已有库。在指纹库页面点击“更新默认库”，从 ModelTrace GitHub 下载并校验后原位更新默认库，保留 ID 和启用状态，无需再次切换；已是最新时不作修改，失败时原库保持不变。自定义库不受影响；默认库被删除时会重新创建。

- **分层管理**：站点 → 多个 API Key → 多个模型，密钥加密存储。
- **检测与归因**：手动或定时运行，每次 3–6 题可配置；展示模型候选、家族概率和各题耗时。
- **在线采集指纹**：在“指纹库 → 采集模型指纹”选择已配置的来源模型、基础库、指纹标识和家族，采集 3–6 份有效回答后保存为新版本，可导出或设为当前库。同标识指纹仅在新版本中替换，原库和历史检测不变；失败或取消不保存部分结果。请使用身份可信的来源模型。采集在基础库的特征空间内生成质心，沿用原校准参数，未重新训练或校准扩充后的候选概率。
- **Juice 检测**：仅 GPT（含 ChatGPT、提供商前缀）模型每轮归因题目结束后检测一次，在归因模型右侧、最近检测浮层和详情中显示。参考 [LINUX DO 的多种问法](https://linux.do/t/topic/2457629)，内置 9 条独立文案（XML、算式、中英文直问）；拒答、空回复或非整数时更换文案，取得整数即停止，每条每轮最多尝试一次。按接口地址＋模型＋具体文案持久化尝试次数和正常次数，优先正常率最高的文案；同率优先成功样本多的，再按默认顺序（XML 优先）。网络、鉴权等接口故障停止本轮且不计入文案正常率，取消立即停止。旧记录及不适用模型显示 `—`，未成功显示“未获取”，不影响归因结果。正常仅指可解析为非负整数，数值为模型自报，并非官方预算指标。
- **糖果检测**：每轮检测额外请求一次固定糖果题，支持单个模型或全部模型单独刷新，在两张模型表、最近检测浮层和详情中展示结果、题目与回复。题目与判分规则参考 [haowang02/codex-candy-eval](https://github.com/haowang02/codex-candy-eval/blob/29127fa5a12fb7654e865f684dcaf55ade181349/codex_candy_eval.py)：回复中出现前后非数字的 `21` 即通过，否则未通过；接口异常显示错误，不影响归因结果。沿用上游的文本匹配判分，不分析推理过程是否正确。通过已配置的模型 API 调用，无需本地 Codex CLI；不复现上游 CLI 的上下文。替换原“是/否检测”，旧记录显示 `—`，重新检测后生成糖果结果。
- **糖果思考等级与用量**：在“检测设置”选择接口默认（初始值）、`none`、`minimal`、`low`、`medium`、`high` 或 `xhigh`，仅作用于糖果请求。显式等级通过 OpenAI Chat Completions 的 `reasoning_effort` 发送，需接口和模型支持；失败时不会去掉等级或切换到不支持该参数的协议。仅支持 Anthropic 协议时请选择接口默认。自动检测和单独刷新均保存当次请求等级及 `usage.completion_tokens_details.reasoning_tokens`，模型表、浮层和详情显示 `Reason Tok`；未返回用量或旧记录显示 `—`，返回 `0` 则显示 `0`，不以总输出 token 估算。修改设置不改变历史记录，记录的等级是请求值，并非服务端执行等级的证明。字段参考 [OpenAI 官方文档](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。
- **结果通知**：Webhook、PushDeer，支持仅失败通知、测试发送和发送记录。
- **本地运行**：Windows、macOS 自包含版本，启动后自动打开浏览器，SQLite 持久化。
- **容器部署**：Docker Compose、GHCR 镜像，推送版本 tag 自动构建 Release。

> 归因概率是当前指纹库内的候选比较，不能独立证明模型真实身份。超过 3 份有效回答时，沿用 ModelTrace 的 3 查询校准参数。

## 快速开始

### 桌面版

从 GitHub Release 下载并完整解压：

| 平台 | 文件 | 启动方式 |
| --- | --- | --- |
| Windows x64 | `TrueModel-win-x64.zip` | 双击 `TrueModel.exe` |
| macOS Intel | `TrueModel-osx-x64.zip` | 双击 `Start TrueModel.command` |
| macOS Apple Silicon | `TrueModel-osx-arm64.zip` | 双击 `Start TrueModel.command` |

无需安装 .NET 或 Python。首次启动在网页创建管理员账号；服务仅监听本机，关闭程序即停止服务。macOS 包尚未签名、公证，首次运行可能需要在系统安全设置中允许。

数据保存在用户本地应用数据目录的 `TrueModel` 文件夹（Windows 通常为 `%LOCALAPPDATA%/TrueModel`），启动窗口显示具体路径。升级仅替换程序目录；备份时同时保留数据库和 `keys` 目录。

### Docker

在同一目录创建 `compose.yml` 和 `.env`，使用 GHCR 镜像 `ghcr.io/aiqinxuancai/truemodel` 部署。

**compose.yml**

```yaml
services:
  truemodel:
    image: ghcr.io/aiqinxuancai/truemodel:${TRUEMODEL_VERSION:-latest}
    restart: unless-stopped
    ports:
      - "${HTTP_PORT:-8080}:8080"
    environment:
      ASPNETCORE_HTTP_PORTS: "8080"
      DataDirectory: /data
      Desktop__Enabled: "false"
      Admin__Username: ${ADMIN_USERNAME:-admin}
      Admin__Password: ${ADMIN_PASSWORD:?请在 .env 中设置管理员密码}
    volumes:
      - ./data:/data
```

**.env**

```dotenv
# 使用 latest，或填写 GHCR 中已发布的版本标签
TRUEMODEL_VERSION=latest
HTTP_PORT=8080
ADMIN_USERNAME=admin
# 首次启动前替换为自己的密码，至少 12 个字符
ADMIN_PASSWORD='Replace-With-Your-Password-123!'
```

**启动与更新**

```bash
docker compose --env-file .env -f compose.yml pull
docker compose --env-file .env -f compose.yml up -d
```

打开 `http://localhost:8080`（远程部署使用服务器地址），用 `.env` 中的账号登录。更改 `HTTP_PORT` 后使用对应端口。更新镜像时再次执行以上两条命令。

数据库、站点配置、检测记录和加密密钥保存在当前目录的 `./data` 中，备份时保留整个目录。管理员环境变量仅用于首次初始化；已有数据库时，修改 `.env` 不会重置账号密码。检测和通知参数在 Web 页面配置。

也可以在源码仓库中本地构建：

```bash
ADMIN_PASSWORD='请替换为至少12位的密码' docker compose -f TrueModel/docker-compose.yml up -d --build
```

此源码构建方式使用 `truemodel-data` 命名卷保存数据。

### 源码运行

安装 .NET 10 SDK 后：

```powershell
$env:Admin__Password = '请替换为至少12位的密码'
dotnet run --project TrueModel
```

访问 `http://localhost:5168`。设置 `Desktop__Enabled=true` 可启用首次设置与自动打开浏览器。

## 空指纹库

在“指纹库 → 新建空指纹库”填写名称即可创建不含模型条目的库。空库沿用内置库的特征参数，可手动新增指纹，或作为在线采集的基础库；支持导出和重新导入。至少添加两个模型指纹后才能设为当前库。

## 通知设置

在 **通知** 页面配置并保存后，可点击“发送测试通知”验证：

| 通道 | 配置 | 发送内容 |
| --- | --- | --- |
| Webhook | HTTP/HTTPS URL | POST JSON：批次、成功数量、耗时、各模型归因摘要 |
| PushDeer | 推送接口和 PushKey | 支持 [官方服务及自建服务](https://github.com/easychen/pushdeer)，发送结果摘要 |

默认关闭通知；可选择仅失败或取消时发送。URL 与 PushKey 加密保存，留空保留原值，可显式清除。通知不包含 API Key 和模型原始响应；失败不会改变检测结果，在“最近发送记录”查看状态。

Webhook 示例：

```json
{"event":"detection.completed","runId":1,"status":"Completed","durationMs":120000,"total":2,"succeeded":2,"results":[]}
```

## 配置与发布

| 环境变量 | 用途 |
| --- | --- |
| `Admin__Username` / `Admin__Password` | 首次初始化管理员 |
| `DataDirectory` | 指定数据与加密密钥存放目录 |
| `Desktop__Enabled` | 桌面模式：仅本机监听、初始化向导 |
| `Desktop__OpenBrowser` | 是否自动打开浏览器，默认开启 |

检测使用 ModelTrace 的请求格式、有效阈值和重试规则，单次请求超时 240 秒。Web 设置保存每次题目数、调度间隔和并发数。

推送 `v*` tag 后，GitHub Actions 运行测试、生成三个桌面发行包并创建 GitHub Release，同时发布 `ghcr.io/<owner>/<repo>:<version>` 和 `latest` 镜像。

```bash
dotnet test TrueModel.slnx -c Release
```

测试包含真实响应的 ModelTrace 归因对照、3/6 题配置、协议回退、管理员鉴权、配置持久化和通知结果；桌面构建额外验证首次启动与重启后的数据保留。
