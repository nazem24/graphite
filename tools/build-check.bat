@echo off
cd /d "%~dp0.."
echo Building... > build-check.log
dotnet build Graphite.sln -c Debug -nologo -clp:NoSummary -v:q >> build-check.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build-check.log
