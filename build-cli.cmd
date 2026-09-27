@echo off
rem ===================================================================
rem  编译命令行版：AcfCore.cs + SteamACFManager.cs
rem  输出 SteamACFManagerCLI.exe（不联网；需要联网补全时用图形版）
rem ===================================================================
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [错误] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
  exit /b 1
)
pushd "%~dp0"
"%CSC%" /nologo /codepage:65001 /target:exe ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /out:SteamACFManagerCLI.exe Localization.cs AcfCore.cs SteamEvidence.cs SteamACFManager.cs
set RC=%ERRORLEVEL%
popd
if %RC% NEQ 0 (echo [失败] 编译出错 & exit /b %RC%)
echo [完成] 已生成 SteamACFManagerCLI.exe
exit /b 0

