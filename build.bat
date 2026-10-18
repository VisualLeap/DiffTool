@echo off
setlocal
set "runTests="
if "%~1"=="" goto argsReady
if /i not "%~1"=="-Test" goto usage
if not "%~2"=="" goto usage
set "runTests=1"

:argsReady
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" set "compiler=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%compiler%" goto missingCompiler
pushd "%~dp0"
if errorlevel 1 exit /b 1
call :build
set "buildResult=%errorlevel%"
popd
exit /b %buildResult%

:build
if not exist "bin" mkdir "bin"
if not exist "bin" exit /b 1
"%compiler%" /nologo /target:winexe /optimize+ /utf8output /out:bin\DiffTool.exe /win32icon:assets\DiffTool.ico /resource:assets\DiffTool.ico,DiffTool.AppIcon /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:Accessibility.dll Program.cs GitRepository.cs DiffView.cs DarkScrollBar.cs GroupedFileList.cs WheelScroll.cs MinimapViewport.cs RecentRepositories.cs RepositoryPicker.cs
if errorlevel 1 goto buildFailed
echo Built "%CD%\bin\DiffTool.exe"
if not defined runTests exit /b 0
"%compiler%" /nologo /target:exe /utf8output /out:bin\DiffTool.Tests.exe /reference:bin\DiffTool.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:Accessibility.dll tests\Tests.cs
if errorlevel 1 goto testBuildFailed
"bin\DiffTool.Tests.exe"
if errorlevel 1 goto testsFailed
exit /b 0

:usage
echo Usage: build.bat [-Test]
exit /b 1

:missingCompiler
echo The .NET Framework C# compiler is required (included with .NET Framework 4.x on Windows).
exit /b 1

:buildFailed
echo Build failed.
exit /b 1

:testBuildFailed
echo Test build failed.
exit /b 1

:testsFailed
echo Tests failed.
exit /b 1
