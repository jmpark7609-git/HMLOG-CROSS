# Builds HMLOG for every OS (needs the .NET 8+ SDK, any host OS can build all targets).
#   .\publish.ps1                 all targets
#   .\publish.ps1 osx-arm64       one target (win-x64, osx-arm64, osx-x64, linux-x64)
# Output: publish\<rid>\ (self-contained, no .NET install needed). For macOS the folder is also packed
# into HMLOG.app (publish\HMLOG-<rid>.app) - unsigned: on first start use right-click > Open
# (or sign / notarize it with an Apple developer account).
param([string[]]$Rids = @("win-x64", "osx-arm64", "osx-x64", "linux-x64"))
$root = $PSScriptRoot
foreach ($rid in $Rids) {
    $out = Join-Path $root "publish\$rid"
    dotnet publish "$root\HMLOG.App\HMLOG.App.csproj" -c Release -r $rid --self-contained -p:PublishSingleFile=false -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    if ($rid -like "osx-*") {
        $app = Join-Path $root "publish\HMLOG-$rid.app"
        if (Test-Path $app) { Remove-Item -Recurse -Force $app }
        New-Item -ItemType Directory -Force "$app\Contents\MacOS", "$app\Contents\Resources" | Out-Null
        Copy-Item -Recurse -Force "$out\*" "$app\Contents\MacOS\"
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>HMLOG</string>
<key>CFBundleDisplayName</key><string>HMLOG</string>
<key>CFBundleIdentifier</key><string>com.owm.hmlog</string>
<key>CFBundleVersion</key><string>1.0</string>
<key>CFBundleExecutable</key><string>HMLOG.App</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@ | Set-Content -Encoding UTF8 "$app\Contents\Info.plist"
        Write-Host "app bundle: $app (copy to the Mac, then: chmod +x HMLOG.app/Contents/MacOS/HMLOG.App)"
    }
}
