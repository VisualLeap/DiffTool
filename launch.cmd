@echo off
setlocal
if not exist "%~dp0bin\DiffTool.exe" (
    call "%~dp0build.bat"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" "%~dp0bin\DiffTool.exe" %*
