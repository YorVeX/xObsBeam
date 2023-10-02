dotnet publish .. -c Release -o ..\publish\win-x64 -r win-x64 /p:DefineConstants=WINDOWS /p:NativeLib=Shared /p:SelfContained=true
pause
