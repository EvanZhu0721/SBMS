# 构建

[English](build.md)

以下命令均在仓库根目录运行。

需要 Rust 1.92 或更高版本、Visual Studio C++ Build Tools、匹配的 Windows Driver Kit、
Inno Setup 6；构建发布包还需要代码签名证书。

```powershell
.\build-installer.ps1 -SigningCertificateThumbprint <thumbprint>
```

这是完整且唯一的发布入口：它会按锁文件构建 Rust 二进制、构建并签名驱动、
生成安装包、验证所有签名，并把安装包输出到
`target\installer`。

开发单个组件时，可分别使用 `cargo build --release --locked --bins` 或
`.\build-driver.ps1`。需要检查完整的未签名构建与打包链时，必须显式使用
`.\build-installer.ps1 -Unsigned`；它仍会编译并验证驱动，然后生成带
`-unsigned` 后缀的冒烟测试产物，不得对外发布。
