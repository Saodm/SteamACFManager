@echo off
rem 第二场景测试流程的一键入口（逻辑在 test-scenario2.ps1，顶部有完整说明）
rem   test-scenario2.cmd 413150              只读预演
rem   test-scenario2.cmd 413150 break        人工造出第二场景（只改 ACF 账目）
rem   test-scenario2.cmd 413150 apply        真正写入修复（先退出 Steam）
rem   test-scenario2.cmd 413150 rollback     还原
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-scenario2.ps1" %*