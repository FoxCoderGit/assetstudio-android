@echo off
setlocal
cd /d "%~dp0"
dotnet build "AssetStudio.Android\AssetStudio.Android.csproj" -c Debug
if errorlevel 1 exit /b %errorlevel%
echo.
echo Build completed.
echo APK: AssetStudio.Android\bin\Debug\net10.0-android\com.codebyfox.assetstudio.dotnet-Signed.apk
