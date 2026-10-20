// NodePath 从 Pi.CodingAgent.Utils 移到了 Pi.Durable.Env（远程执行环境也要用它，见
// docs/env-package-migration.md）。这个别名让本项目的 57 个调用点无需逐文件改 using。
global using NodePath = Pi.Durable.Env.NodePath;
