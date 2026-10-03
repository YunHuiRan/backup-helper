@echo off
chcp 936 >nul 2>nul
setlocal
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [错误] 找不到 .NET Framework 自带的 C# 编译器 csc.exe
    exit /b 1
)

echo 正在编译 BackupHelper.exe ...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"BackupHelper.exe" BackupHelper.cs BackupEngine.cs
if errorlevel 1 (
    echo [错误] 编译失败
    exit /b 1
)

echo 编译完成：%~dp0BackupHelper.exe
endlocal
