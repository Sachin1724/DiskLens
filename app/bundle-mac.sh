#!/bin/sh
# Wrap the published macOS binary in a double-clickable DiskLens.app.
#   dotnet publish app -c Release -r osx-arm64 && sh app/bundle-mac.sh
set -e
here=$(cd "$(dirname "$0")" && pwd)
pub="$here/bin/Release/net8.0/osx-arm64/publish"
app="$here/bin/DiskLens.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$pub/." "$app/Contents/MacOS/"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>DiskLens</string>
  <key>CFBundleIdentifier</key><string>app.disklens</string>
  <key>CFBundleExecutable</key><string>DiskLens</string>
  <key>CFBundleVersion</key><string>2.2.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>DiskLens asks Finder to move files to the Trash.</string>
</dict></plist>
PLIST
echo "built $app"
