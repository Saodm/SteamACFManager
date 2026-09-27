@echo off
rem ===================================================================
rem  编译图形界面版（主程序）：AcfCore.cs + SteamACFManagerGUI.cs
rem  依赖 .NET Framework 4.5+ 自带的 csc.exe，无需安装 SDK
rem ===================================================================
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [错误] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
  exit /b 1
)
pushd "%~dp0"
"%CSC%" /nologo /codepage:65001 /target:winexe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll ^
  /out:SteamACFManager.exe Localization.cs AcfCore.cs SteamACFManagerGUI.cs
set RC=%ERRORLEVEL%
popd
if %RC% NEQ 0 (echo [失败] 编译出错 & exit /b %RC%)
echo [完成] 已生成 SteamACFManager.exe
exit /b 0

