@echo off
setlocal
title Update Stash
cd /d "%~dp0"

where git >nul 2>nul
if errorlevel 1 (
    echo.
    echo Git was not found on this PC.
    echo Install Git for Windows and run this file again.
    echo.
    pause
    exit /b 1
)

echo.
echo Stash updater
echo.

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo This folder is not a Git repository.
    echo.
    pause
    exit /b 1
)

git status --porcelain > "%TEMP%\stash_git_status.txt"
for %%A in ("%TEMP%\stash_git_status.txt") do if %%~zA GTR 0 (
    echo Local changes were found.
    echo Stash will not update over local work.
    echo.
    git status --short
    del "%TEMP%\stash_git_status.txt" >nul 2>nul
    echo.
    pause
    exit /b 1
)
del "%TEMP%\stash_git_status.txt" >nul 2>nul

set "TARGET_BRANCH=%~1"
if "%TARGET_BRANCH%"=="" set "TARGET_BRANCH=move-workflow"

echo Working branch: "%TARGET_BRANCH%"
echo.
echo Fetching branches from GitHub...
git fetch origin --prune
if errorlevel 1 goto :update_failed

git show-ref --verify --quiet "refs/heads/%TARGET_BRANCH%"
if errorlevel 1 (
    git show-ref --verify --quiet "refs/remotes/origin/%TARGET_BRANCH%"
    if errorlevel 1 (
        echo.
        echo Branch "%TARGET_BRANCH%" was not found locally or on GitHub.
        echo.
        pause
        exit /b 1
    )

    echo Creating local branch "%TARGET_BRANCH%"...
    git switch --track "origin/%TARGET_BRANCH%"
    if errorlevel 1 goto :update_failed
) else (
    for /f "delims=" %%B in ('git branch --show-current') do set "CURRENT_BRANCH=%%B"
    if /i not "%CURRENT_BRANCH%"=="%TARGET_BRANCH%" (
        echo Switching to "%TARGET_BRANCH%"...
        git switch "%TARGET_BRANCH%"
        if errorlevel 1 goto :update_failed
    )
)

echo.
echo Updating "%TARGET_BRANCH%"...
git pull --ff-only origin "%TARGET_BRANCH%"
if errorlevel 1 goto :update_failed

echo.
echo Stash is up to date on branch "%TARGET_BRANCH%".
echo Launching Stash...
echo.

if not exist "%~dp0main.py" (
    echo main.py was not found yet.
    echo The updater is ready; the app will launch automatically once main.py exists.
    echo.
    pause
    exit /b 0
)

where pyw >nul 2>nul
if not errorlevel 1 (
    start "" pyw -3 "%~dp0main.py"
    exit /b 0
)

where pythonw >nul 2>nul
if not errorlevel 1 (
    start "" pythonw "%~dp0main.py"
    exit /b 0
)

where py >nul 2>nul
if not errorlevel 1 (
    start "" py -3 "%~dp0main.py"
    exit /b 0
)

where python >nul 2>nul
if not errorlevel 1 (
    start "" python "%~dp0main.py"
    exit /b 0
)

echo Python was not found on this PC.
echo.
pause
exit /b 1

:update_failed
echo.
echo Update failed. No files were intentionally overwritten.
echo Run "git status" to check the repository state.
echo.
pause
exit /b 1
