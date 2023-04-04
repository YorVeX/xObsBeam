#!/bin/sh
dotnet publish .. -c Release -o ../publish/linux-x64 -r linux-x64 /p:NativeLib=Shared /p:SelfContained=true
