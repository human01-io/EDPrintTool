@echo off
echo ================================================
echo   EDPrintTool - Windows Setup
echo ================================================
echo.

:: Check if Node.js is installed
where node >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] Node.js is not installed!
    echo.
    echo Please install Node.js from: https://nodejs.org
    echo Download the LTS version, run the installer, then re-run this script.
    echo.
    pause
    exit /b 1
)

echo [OK] Node.js found:
node --version
echo.

:: Install dependencies
echo [1/3] Installing dependencies...
call npm install
if %errorlevel% neq 0 (
    echo [ERROR] npm install failed!
    pause
    exit /b 1
)

:: Install node-printer for Windows USB support
echo [2/3] Installing Windows printer driver (node-printer)...
call npm install @thiagoelg/node-printer
if %errorlevel% neq 0 (
    echo.
    echo [WARNING] node-printer failed to install.
    echo You may need to install Windows Build Tools first:
    echo   npm install --global windows-build-tools
    echo Or install Visual Studio Build Tools from:
    echo   https://visualstudio.microsoft.com/visual-cpp-build-tools/
    echo.
    echo Network printing will still work. USB printing requires node-printer.
    echo.
)

echo [3/3] Setup complete!
echo.
echo ================================================
echo.
echo   To start the desktop app:
echo     npm start        (opens Electron window + tray icon)
echo.
echo   To start server-only mode:
echo     npm run server   (localhost:8189 in browser)
echo.
echo   To build a Windows installer (.exe):
echo     npm run build:win
echo.
echo ================================================
echo.
pause
