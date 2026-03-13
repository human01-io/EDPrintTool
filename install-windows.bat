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
echo [1/2] Installing dependencies...
call npm install
if %errorlevel% neq 0 (
    echo [ERROR] npm install failed!
    pause
    exit /b 1
)

echo [2/2] Setup complete!
echo.
echo ================================================
echo.
echo   To start the server:
echo     npm start          (localhost:8189 in browser)
echo.
echo   To start in dev mode (auto-reload):
echo     npm run dev
echo.
echo ================================================
echo.
pause
