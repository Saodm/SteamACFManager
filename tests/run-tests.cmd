@echo off
rem 启动回归测试（转交给 PowerShell 脚本，便于正确处理中文输出与断言）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" %*
exit /b %ERRORLEVEL%
