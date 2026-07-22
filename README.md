#因发现更为成熟的类似插件，现暂停对该项目的开发


## CS2 QR Login — CounterStrikeSharp 插件

CS2 游戏内扫码登录插件。玩家输入 `!login`，插件将 SteamID 发送到你的网站后端，生成一次性登录链接并转为二维码，直接在游戏屏幕上显示。

## 工作流程

```
!login (玩家聊天输入)
  ↓
GET / POST { steam_id, player_name, ip_address } → 你的后端
  ↓
后端返回 { "login_url": "https://..." }
  ↓
QRCoder 本地生成二维码 (PNG data URI + ASCII)
  ↓
三重显示：
  ① 屏幕中央 — PNG 二维码图片 (手机可扫描)
  ② 控制台菜单 (CS2MenuManager) — ASCII 二维码 + URL + 重显/关闭
  ③ 聊天框 — URL 链接 (备选)
```

## 依赖

| 包 | 版本 | 用途 |
|---|---|---|
| [CounterStrikeSharp.API](https://www.nuget.org/packages/CounterStrikeSharp.API) | 1.0.371 | CS2 插件框架 |
| [CS2MenuManager](https://github.com/schwarper/CS2MenuManager) | 1.0.43 | 控制台菜单 UI |
| [QRCoder](https://github.com/codebude/QRCoder) | 1.8.0 | 二维码生成 |

## 安装

1. 编译插件：
   ```bash
   dotnet build -c Release
   ```

2. 将 `bin/Release/net10.0/cs2-basic-plugin.dll` 放到服务器 `addons/counterstrikesharp/plugins/cs2-basic-plugin/`

3. 安装 [CS2MenuManager](https://github.com/schwarper/CS2MenuManager/releases) — 将 zip 解压到 `addons/counterstrikesharp/`

4. 重启服务器，插件会自动生成配置文件 `configs/plugins/cs2-basic-plugin/config.json`

## 配置

```json
{
  "ConfigVersion": 1,
  "BackendUrl": "http://localhost:3000/api/auth/login",
  "HttpTimeoutSeconds": 10,
  "CommandName": "css_login",
  "QrPixelsPerModule": 4,
  "QrDisplaySeconds": 30,
  "QrImageSize": 220
}
```

| 字段 | 默认值 | 说明 |
|---|---|---|
| `BackendUrl` | `http://localhost:3000/api/auth/login` | 后端登录接口地址 |
| `HttpTimeoutSeconds` | `10` | HTTP 请求超时秒数 |
| `CommandName` | `css_login` | 触发指令名 |
| `QrPixelsPerModule` | `4` | 二维码模块像素密度 |
| `QrDisplaySeconds` | `30` | 菜单显示秒数 |
| `QrImageSize` | `220` | 屏幕二维码图片像素大小 |

## 后端 API 规范

插件发送 POST 请求到 `BackendUrl`：

```json
// Request
{
  "steam_id": "7656119XXXXXXXXXX",
  "player_name": "cyqmq",
  "ip_address": "192.168.1.1"
}

// Response (200 OK)
{
  "login_url": "https://your-site.com/auth?token=xxx"
}
```

`login_url` 即为二维码内容，玩家扫码后打开该链接即完成登录。

## 测试

1. 启动模拟后端：
   ```bash
   python3 -c "
   from http.server import HTTPServer, BaseHTTPRequestHandler
   import json
   class H(BaseHTTPRequestHandler):
       def do_POST(self):
           body = self.rfile.read(int(self.headers['Content-Length']))
           print('收到:', json.loads(body))
           self.send_response(200)
           self.send_header('Content-Type', 'application/json')
           self.end_headers()
           self.wfile.write(json.dumps({'login_url': 'https://example.com/auth?token=test'}).encode())
   HTTPServer(('0.0.0.0', 3000), H).serve_forever()
   "
   ```

2. 进游戏输入 `!login`

## 构建

```bash
dotnet build
```
