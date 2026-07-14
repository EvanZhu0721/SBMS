# SBMS 虚拟显示驱动安装说明（已弃用）

本文原有流程会直接修改全局启动策略、信任存储、系统目录、PnP/Enum 状态和 Driver Store，不具备事务化快照、独立恢复通道、SYSTEM watchdog 或按 Run ID 约束的回滚所有权，不能再作为安装或验收指南。

不要从历史说明中手工执行 BCD、完整性检查、证书、注册表或驱动安装命令。尤其不允许通过关闭完整性检查来绕过驱动签名要求。

当前规范：

- [安全实机实验室设计](docs/SAFE-HARDWARE-LAB.md)
- [硬件验收 observer](docs/HARDWARE-VALIDATION.md)

第一阶段仅实现并测试安全实验室脚本，不在当前机器上执行系统变更。驱动安装必须等待 Gate A、Gate B、Gate C 依次通过，并使用专用测试机、独立恢复显示、实测 SSH、BitLocker recovery readiness 和 SYSTEM watchdog。
