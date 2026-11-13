@echo off
setlocal
if exist "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\bin\Release\net10.0-windows\HUMofA.exe" (
    "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\bin\Release\net10.0-windows\HUMofA.exe" %*
    exit /b %ERRORLEVEL%
)
if exist "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\bin\Debug\net10.0-windows\HUMofA.exe" (
    "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\bin\Debug\net10.0-windows\HUMofA.exe" %*
    exit /b %ERRORLEVEL%
)
dotnet run --project "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\HUMofA.csproj" -- %*
