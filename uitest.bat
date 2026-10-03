@echo off
chcp 65001 >nul 2>nul
setlocal
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [错误] 找不到 csc.exe
    exit /b 1
)

echo 正在编译测试程序 UiTest.exe ...
"%CSC%" /nologo /target:exe /codepage:65001 /main:BackupHelper.UiTest /out:"UiTest.exe" UiTest.cs BackupHelper.cs BackupEngine.cs
if errorlevel 1 (
    echo [错误] 编译失败
    exit /b 1
)

echo 开始运行界面逻辑测试 ...
echo.
"%~dp0UiTest.exe"
set "CODE=%errorlevel%"

del /q "%~dp0UiTest.exe" >nul 2>nul
echo.
if "%CODE%"=="0" (echo 测试全部通过。) else (echo 有测试失败，返回码 %CODE%。)
exit /b %CODE%
