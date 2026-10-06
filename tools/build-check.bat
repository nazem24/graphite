@echo off
set "ProgramData=C:\ProgramData"
set "ProgramFiles=C:\Program Files"
set "ProgramFiles(x86)=C:\Program Files (x86)"
set "ProgramW6432=C:\Program Files"
set "CommonProgramFiles=C:\Program Files\Common Files"
set "CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files"
set "SystemRoot=C:\WINDOWS"
set "SystemDrive=C:"
set "windir=C:\WINDOWS"
set "TMP=C:\Users\micha\AppData\Local\Temp"
set "TEMP=C:\Users\micha\AppData\Local\Temp"
set "HOME=C:\Users\micha"
"C:\Program Files\dotnet\x64\dotnet.exe" build "C:\Users\micha\Documents\Claude Cowork Area\Cowork PDF viewer\src\Graphite.App\Graphite.App.csproj" -c Debug --nologo -v q -nr:false
