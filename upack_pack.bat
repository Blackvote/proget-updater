@echo off
:: Require: upack.exe in PATH from https://github.com/Inedo/upack/releases
for /f "tokens=3delims=<>" %%a in ('findstr "Version" "updater.csproj"') do set csproj-version=%%a
echo In updater.csproj: Version = "%csproj-version%"

echo Prepare release dir: .\bin\Release\upack
del /F /S /Q .\bin\Release\upack
mkdir .\bin\Release\upack
copy /Y .\updater2\bin\Release\netcoreapp3.1\publish.full\* .\bin\Release\upack\
copy /Y .\bin\Release\netcoreapp3.1\publish.full\* .\bin\Release\upack\

echo Create upack-package
upack.exe pack ./bin/Release/upack --targetDirectory=./bin/Release/ --manifest=./upack.json
