@echo off
rem Сборка FreeDom Launcher 1.0.0 (нужен .NET SDK 10): получится publish\FreeDomLauncher.exe
dotnet restore FreeDomLauncher.sln || goto :err
dotnet build FreeDomLauncher.sln -c Release --no-restore || goto :err
dotnet test FreeDomLauncher.Tests\FreeDomLauncher.Tests.csproj -c Release --no-build || goto :err
dotnet publish FreeDomLauncher.App\FreeDomLauncher.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o publish || goto :err
echo.
echo Готово: publish\FreeDomLauncher.exe
exit /b 0
:err
echo Сборка не удалась. Скопируйте текст ошибок выше и отправьте разработчику.
exit /b 1
