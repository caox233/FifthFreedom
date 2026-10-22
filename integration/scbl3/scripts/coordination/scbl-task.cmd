@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "SCBL_TASK_SCRIPT=%~dp0scbl_task.py"
if not exist "%SCBL_TASK_SCRIPT%" (
  >&2 echo SCBL task entry is missing: "%SCBL_TASK_SCRIPT%"
  exit /b 2
)

set "SCBL_TASK_PYTHON="
if defined USERPROFILE set "SCBL_PRIMARY_PYTHON=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if defined SCBL_PRIMARY_PYTHON if exist "%SCBL_PRIMARY_PYTHON%" call :consider_python "%SCBL_PRIMARY_PYTHON%"

if defined SCBL_TASK_PYTHON goto run_task
for /f "usebackq delims=" %%P in (`"%SystemRoot%\System32\where.exe" python.exe 2^>nul`) do call :consider_python "%%~fP"
for /f "usebackq delims=" %%P in (`"%SystemRoot%\System32\where.exe" python3.exe 2^>nul`) do call :consider_python "%%~fP"

if not defined SCBL_TASK_PYTHON if defined LOCALAPPDATA if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe" >&2 echo SCBL task entry rejected WindowsApps Python alias: "%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe"
if not defined SCBL_TASK_PYTHON if defined LOCALAPPDATA if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\python3.exe" >&2 echo SCBL task entry rejected WindowsApps Python alias: "%LOCALAPPDATA%\Microsoft\WindowsApps\python3.exe"

if not defined SCBL_TASK_PYTHON (
  >&2 echo SCBL task entry could not locate the current codex-primary-runtime Python.
  exit /b 2
)

:run_task
"%SCBL_TASK_PYTHON%" "%SCBL_TASK_SCRIPT%" %*
set "SCBL_TASK_EXIT=%ERRORLEVEL%"
exit /b %SCBL_TASK_EXIT%

:consider_python
set "SCBL_CANDIDATE=%~f1"
echo(%SCBL_CANDIDATE%| "%SystemRoot%\System32\findstr.exe" /i /c:"\WindowsApps\" >nul
if not errorlevel 1 (
  >&2 echo SCBL task entry rejected WindowsApps Python alias: "%SCBL_CANDIDATE%"
  exit /b 0
)
echo(%SCBL_CANDIDATE%| "%SystemRoot%\System32\findstr.exe" /i /c:"\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" >nul
if errorlevel 1 exit /b 0
if exist "%SCBL_CANDIDATE%" set "SCBL_TASK_PYTHON=%SCBL_CANDIDATE%"
exit /b 0
