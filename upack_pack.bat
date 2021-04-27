@echo off
:: Require: upack.exe in PATH from https://github.com/Inedo/upack/releases
for /f "tokens=3delims=<>" %%a in ('findstr "Version" "updater.csproj"') do set csproj-version=%%a
echo In updater.csproj: Version = "%csproj-version%"

echo Create upack-package
upack.exe pack ./bin/Release/netcoreapp3.1/publish/ --targetDirectory=./bin/Release/ --manifest=./upack.json
