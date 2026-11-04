#!/bin/bash
# Genera RemotoFacil-x86_64.AppImage en esta carpeta.
# Necesita el SDK de .NET 8 (~/.dotnet/dotnet o dotnet en el PATH) e internet la primera vez (appimagetool).
set -euo pipefail
cd "$(dirname "$0")"
DOTNET=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
TRABAJO=compilacion
APPDIR=$TRABAJO/AppDir
HERRAMIENTA=$TRABAJO/appimagetool-x86_64.AppImage

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/lib/remotofacil"

echo "### Compilando"
"$DOTNET" publish RemotoFacil.csproj -c Release -r linux-x64 --self-contained -o "$APPDIR/usr/lib/remotofacil" -v quiet -nologo

echo "### Preparando el AppDir"
# AppRun sin tocar LD_LIBRARY_PATH: .NET y Avalonia cargan sus bibliotecas desde su propia carpeta
cat > "$APPDIR/AppRun" <<'FIN'
#!/bin/sh
exec "$(dirname "$(readlink -f "$0")")/usr/lib/remotofacil/RemotoFacil" "$@"
FIN
chmod +x "$APPDIR/AppRun"
cat > "$APPDIR/remotofacil.desktop" <<'FIN'
[Desktop Entry]
Type=Application
Name=RemotoFácil
Comment=Configura el acceso remoto a este PC (Tailscale + Sunshine + Moonlight)
Exec=RemotoFacil
Icon=remotofacil
Terminal=false
Categories=Network;RemoteAccess;
FIN
cp Recursos/logo256.png "$APPDIR/remotofacil.png"
ln -sf remotofacil.png "$APPDIR/.DirIcon"

if [ ! -x "$HERRAMIENTA" ]; then
    echo "### Descargando appimagetool"
    curl -fsSL -o "$HERRAMIENTA" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$HERRAMIENTA"
fi

# Que cualquier usuario pueda leer y ejecutar (si no, depende del umask de quien empaqueta)
chmod -R a+rX "$APPDIR"
chmod 755 "$APPDIR/AppRun" "$APPDIR/usr/lib/remotofacil/RemotoFacil"

echo "### Empaquetando"
# --appimage-extract-and-run: así appimagetool funciona aunque no haya FUSE
ARCH=x86_64 "$HERRAMIENTA" --appimage-extract-and-run --comp zstd "$APPDIR" RemotoFacil-x86_64.AppImage
ls -la RemotoFacil-x86_64.AppImage
