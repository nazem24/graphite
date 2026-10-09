@echo off
cd /d "%~dp0.."
echo Testing... > test-check.log
dotnet test Graphite.sln -c Debug --nologo -v q -nr:false >> test-check.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> test-check.log
