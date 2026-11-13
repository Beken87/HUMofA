$releaseExe = "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\bin\Release\net10.0-windows\HUMofA.exe"
$debugExe = "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\bin\Debug\net10.0-windows\HUMofA.exe"
$projPath = "c:\Users\beken\Desktop\Files\HUMofA\src\HUMofA\HUMofA.csproj"

if (Test-Path $releaseExe) {
    & $releaseExe @args
} elseif (Test-Path $debugExe) {
    & $debugExe @args
} else {
    dotnet run --project $projPath -- @args
}

