# ClashYamlParser
提供类似Clash for Windows中parser功能的服务器应用程序。| A server application provides clash parser feature likes Clash for Windows. ([Switch to English README](https://github.com/LeoChen98/ClashYamlParser/blob/master/README.md))

![GitHub License](https://img.shields.io/github/license/LeoChen98/ClashYamlParser)
![GitHub Release](https://img.shields.io/github/v/release/LeoChen98/ClashYamlParser)
![GitHub Repo stars](https://img.shields.io/github/stars/LeoChen98/ClashYamlParser)

## 功能
- ### parsers
    - [x] prepend-rlues
    - [x] append-rules
    - [x] prepend-proxies
    - [x] append-proxies
    - [x] prepend-proxy-groups
    - [x] append-proxy-groups
    - [ ] command
- ### 杂项
    - [x] Subscription-Userinfo (用于在Clash for Windows中显示使用数据和有效期)
    - [x] 服务器日志
    - [x] SSL 连接 (HTTPS)

## 依赖和兼容性
- [.NET 8.0 运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0#runtime-8.0.6)
- 仅在 `Windows 11` 和 `Windows Server 2012`下测试通过。理论上具有全平台兼容性。（需要自行编译）。
- 仅在 `Clash for Windows` 和 `Clash for Android`中测试通过。理论上可用于Clash的所有分支版本
 
## 开始使用
*一下提到的所有路径均为相对于 `ClashYamlParser.exe` 的根目录*.
- 下载 [最新的发行](https://github.com/LeoChen98/ClashYamlParser/releases/latest) 文件。
- 根据 [Microsoft Learn](https://learn.microsoft.com/zh-cn/dotnet/fundamentals/runtime-libraries/system-net-httplistener) 的格式设置`domain.txt`，每行一个域名。
- 从Clash for Windows中复制parsers配置，新建并粘贴到 `parser.yaml`中。
- （如果需要使用SSL连接）使用 `netsh` 工具将对应证书绑定到端口。
- 用管理员权限运行 `ClashYamlParser.exe` 。
- 通过URL订阅配置。例如： `http(s)://example.com:port/clash/?url=your origin clash profile url`.

## 贡献
欢迎提出Issue和Pull Request，最好是直接提Pull Request。（懒死了.jpg）

## 第三方代码清单
- ### YamlDotNet(15.3.0)
    基于MIT协议授权。
- ### ChatGPT4
    部分代码由ChatGPT4生成。