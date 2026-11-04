using System;
using System.IO;
using System.Linq;

namespace RemotoFacil
{
    /// <summary>
    /// Que RemotoFácil quede "instalado" como usuario normal: el AppImage copiado a una ruta fija
    /// (la que usa Sunshine), su lanzador en el menú de aplicaciones y, si se quiere, en el escritorio.
    /// </summary>
    static class Integracion
    {
        public static bool YaConfigurado => File.Exists(Rutas.MarcaConfigurado) && Tailscale.Instalado && Sunshine.Instalado;

        public static void MarcarConfigurado()
        {
            Directory.CreateDirectory(Rutas.Config);
            File.WriteAllText(Rutas.MarcaConfigurado, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        /// Copia los archivos del programa a ~/.local/share/remotofacil/app y crea el lanzador del menú
        public static void Instalar()
        {
            var origen = Path.GetFullPath(Rutas.CarpetaActual).TrimEnd('/');
            var destino = Path.GetFullPath(Rutas.CarpetaApp).TrimEnd('/');
            if (origen != destino)
            {
                // Se copia aparte y luego se cambia de sitio, por si hay otra copia ejecutándose desde la carpeta vieja
                var nueva = destino + ".nueva";
                if (Directory.Exists(nueva)) Directory.Delete(nueva, true);
                CopiarCarpeta(origen, nueva);
                if (Directory.Exists(destino)) Directory.Delete(destino, true);
                Directory.Move(nueva, destino);
            }

            // Icono
            Directory.CreateDirectory(Path.GetDirectoryName(Rutas.Icono));
            using (var s = typeof(Integracion).Assembly.GetManifestResourceStream("logo256.png"))
            using (var f = File.Create(Rutas.Icono))
                s?.CopyTo(f);

            Directory.CreateDirectory(Path.GetDirectoryName(Rutas.Lanzador));
            File.WriteAllText(Rutas.Lanzador, ContenidoLanzador());
            Sistema.Ejecutar("update-desktop-database", new[] { Path.GetDirectoryName(Rutas.Lanzador) }, 15000);
        }

        static string ContenidoLanzador() =>
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=RemotoFácil\n" +
            "Comment=Centro de control del acceso remoto a este PC\n" +
            $"Exec=\"{Rutas.EjecutableInstalado}\"\n" +
            "Icon=remotofacil\n" +
            "Terminal=false\n" +
            "Categories=Network;RemoteAccess;\n" +
            "StartupWMClass=RemotoFacil\n";

        /// File.Copy en Linux conserva los permisos, así que el ejecutable sigue siéndolo
        static void CopiarCarpeta(string origen, string destino)
        {
            Directory.CreateDirectory(destino);
            foreach (var f in Directory.GetFiles(origen)) File.Copy(f, Path.Combine(destino, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(origen)) CopiarCarpeta(d, Path.Combine(destino, Path.GetFileName(d)));
        }

        public static string AccesoEscritorio => Path.Combine(Rutas.Escritorio, "remotofacil.desktop");

        public static void AccesoEnEscritorio(bool crear)
        {
            var lnk = AccesoEscritorio;
            if (!crear) { if (File.Exists(lnk)) File.Delete(lnk); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            File.WriteAllText(lnk, ContenidoLanzador());
            File.SetUnixFileMode(lnk, File.GetUnixFileMode(lnk) | UnixFileMode.UserExecute);
            // GNOME (Nautilus/DING) solo ejecuta lanzadores del escritorio marcados como de confianza
            Sistema.Ejecutar("gio", new[] { "set", lnk, "metadata::trusted", "true" }, 10000);
        }

        public static void Desinstalar()
        {
            foreach (var f in new[] { Rutas.Lanzador, Rutas.Icono, AccesoEscritorio })
                if (File.Exists(f)) File.Delete(f);
            Sistema.Ejecutar("update-desktop-database", new[] { Path.GetDirectoryName(Rutas.Lanzador) }, 15000);
        }
    }
}
