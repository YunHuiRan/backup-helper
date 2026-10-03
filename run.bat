@echo off
cd /d "%~dp0"
if not exist "BackupHelper.exe" (
    echo 还没有编译，正在调用 build.bat ...
    call "%~dp0build.bat"
    if errorlevel 1 exit /b 1
)
start "" "%~dp0BackupHelper.exe"
