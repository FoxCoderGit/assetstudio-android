@echo off
setlocal
cd /d "%~dp0"
set APK=AssetStudio.Android\bin\Debug\net10.0-android\com.codebyfox.assetstudio.dotnet-Signed.apk
if not exist "%APK%" (
  echo APK not found. Build first.
  exit /b 1
)
adb devices
adb install -r "%APK%"
