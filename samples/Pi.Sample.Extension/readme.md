# Pi.Sample.Extension

一个可编译的最小扩展：注册 `word_count` 工具、`/wordcount` 命令，并在状态栏留一行字。用来对照
`src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md` 里的接口约定。

## 用法

```bash
dotnet build samples/Pi.Sample.Extension

# 项目级（只对这个仓库生效）
mkdir -p .pi/extensions
cp samples/Pi.Sample.Extension/bin/Debug/net10.0/Pi.Sample.Extension.dll .pi/extensions/

# 或者全局
cp samples/Pi.Sample.Extension/bin/Debug/net10.0/Pi.Sample.Extension.dll ~/.pi/extensions/
```

重启 pi 后：

- 模型多了一个 `word_count` 工具（`text` 参数，返回词数与字符数）；
- 输入 `/wordcount 一二三四` 会在状态栏显示 `words=1`。

## 为什么只拷一个 dll

`Pi.Sample.Extension.csproj` 的 `ProjectReference` 带 `Private="false"`：`Pi.CodingAgent.dll`
这类契约程序集由宿主解析，扩展目录里不该出现私有副本——否则会加载出第二份同名程序集，类型身份
对不上，`IExtensionEntry` 也就认不出来了。

扩展如果自己有第三方依赖，把那些 dll 一起放进同一个目录即可。

## 入口契约

程序集里**恰好一个** public、非抽象的 `IExtensionEntry` 实现；它的 `Factory` 就是上游 TypeScript
扩展的 default export。多于一个会加载失败（loader 无法判断用哪个）。
