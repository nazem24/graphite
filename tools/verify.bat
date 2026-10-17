@echo off
cd /d "%~dp0.."
rem Builds the solution and runs the tests; output goes to verify.log in the repo root.
rem Builds into %TEMP%\graphite-verify so a running Graphite (which locks bin\...\Graphite.exe) is never in the way.
set "OUT=%TEMP%\graphite-verify\\"
echo === BUILD === > verify.log
dotnet build Graphite.sln -c Debug -nologo -clp:NoSummary -v:q "-p:BaseOutputPath=%OUT%" >> verify.log 2>&1
echo BUILD_EXITCODE=%ERRORLEVEL% >> verify.log
echo === TEST === >> verify.log
set "GRAPHITE_DUMP=1"
dotnet --version >> verify.log 2>&1
dotnet test src\Graphite.Tests\Graphite.Tests.csproj -c Debug --nologo -v m --logger "console;verbosity=normal" "-p:BaseOutputPath=%OUT%" >> verify.log 2>&1
echo TEST_EXITCODE=%ERRORLEVEL% >> verify.log
echo === DONE === >> verify.log
