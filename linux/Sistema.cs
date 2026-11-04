using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemotoFacil
{
    static class Rutas
    {
        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string variable, string porDefecto)
        {
            var v = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrEmpty(v) ? Path.Combine(Home, porDefecto) : v;
        }

        public static string Config => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "remotofacil");
        public static string Datos => Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "remotofacil");
        public static string Estado => Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "remotofacil");
        public static string Cache => Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "remotofacil");

        public static string MarcaConfigurado => Path.Combine(Config, "configurado.txt");
        public static string Credenciales => Path.Combine(Config, "sunshine.json");
        public static string Tema => Path.Combine(Config, "tema.txt");
        public static string Instalados => Path.Combine(Config, "instalados.txt");
        public static string Energia => Path.Combine(Config, "energia.txt");
        public static string PantallasElegidas => Path.Combine(Config, "pantallas.txt");
        public static string EstadoPantallas => Path.Combine(Estado, "estado-pantallas.json");
        public static string Registro => Path.Combine(Estado, "registro.txt");

        /// Copia "instalada" del contenido del AppImage: ruta fija para Sunshine y los accesos directos,
        /// y sin depender de FUSE (que un AppImage necesita cada vez que se abre)
        public static string CarpetaApp => Path.Combine(Datos, "app");
        public static string EjecutableInstalado => Path.Combine(CarpetaApp, "RemotoFacil");
        public static string Lanzador => Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "applications", "remotofacil.desktop");
        public static string Icono => Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "icons", "hicolor", "256x256", "apps", "remotofacil.png");

        /// Carpeta con los archivos del programa que se está ejecutando (dentro del AppImage montado, si lo es)
        public static string CarpetaActual => AppContext.BaseDirectory;

        /// Carpeta "Escritorio" del usuario, según xdg-user-dirs (en español suele ser ~/Escritorio)
        public static string Escritorio
        {
            get
            {
                var (cod, salida) = Sistema.Ejecutar("xdg-user-dir", new[] { "DESKTOP" }, 5000);
                var d = salida.Trim();
                return cod == 0 && d.Length > 0 && d != Home ? d : Path.Combine(Home, "Desktop");
            }
        }
    }

    static class Registro
    {
        public static bool Desactivado;

        public static void Escribir(string texto)
        {
            if (Desactivado) return;
            try
            {
                Directory.CreateDirectory(Rutas.Estado);
                var f = new FileInfo(Rutas.Registro);
                if (f.Exists && f.Length > 512 * 1024) f.Delete();
                File.AppendAllText(Rutas.Registro, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {texto}\n");
            }
            catch { }
        }
    }

    enum Familia { Debian, Fedora, Arch, Suse, SteamOS, Inmutable, Desconocida }

    enum Escritorio { KDE, GNOME, Hyprland, Sway, X11, Otro }

    /// Qué Linux es y con qué escritorio
    class Distro
    {
        public string Id = "", IdLike = "", Nombre = "Linux", Version = "", Codigo = "", CodigoUbuntu = "", CodigoDebian = "";
        public Familia Familia;

        public static readonly Distro Actual = Leer();

        static Distro Leer()
        {
            var d = new Distro();
            var campos = new Dictionary<string, string>();
            foreach (var ruta in new[] { "/etc/os-release", "/usr/lib/os-release" })
            {
                if (!File.Exists(ruta)) continue;
                foreach (var linea in File.ReadAllLines(ruta))
                {
                    int i = linea.IndexOf('=');
                    if (i > 0) campos[linea.Substring(0, i)] = linea.Substring(i + 1).Trim().Trim('"', '\'');
                }
                break;
            }
            string C(string k) => campos.TryGetValue(k, out var v) ? v : "";
            d.Id = C("ID").ToLowerInvariant();
            d.IdLike = C("ID_LIKE").ToLowerInvariant();
            d.Nombre = C("PRETTY_NAME") is var n && n.Length > 0 ? n : C("NAME");
            d.Version = C("VERSION_ID");
            d.Codigo = C("VERSION_CODENAME");
            d.CodigoUbuntu = C("UBUNTU_CODENAME");
            d.CodigoDebian = C("DEBIAN_CODENAME");

            bool Es(params string[] ids) => ids.Any(x => d.Id == x || (" " + d.IdLike + " ").Contains(" " + x + " "));
            if (d.Id == "steamos") d.Familia = Familia.SteamOS;
            // Fedora Atomic, Bazzite, Kinoite…: el sistema es de solo lectura
            else if (File.Exists("/run/ostree-booted")) d.Familia = Familia.Inmutable;
            else if (Es("arch")) d.Familia = Familia.Arch;
            else if (Es("debian", "ubuntu")) d.Familia = Familia.Debian;
            else if (Es("fedora", "rhel")) d.Familia = Familia.Fedora;
            else if (Es("suse", "opensuse", "opensuse-tumbleweed", "opensuse-leap")) d.Familia = Familia.Suse;
            else d.Familia = Familia.Desconocida;
            return d;
        }

        public static Escritorio Escritorio
        {
            get
            {
                var e = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToUpperInvariant();
                if (e.Contains("KDE")) return Escritorio.KDE;
                if (e.Contains("GNOME")) return Escritorio.GNOME;
                if (e.Contains("HYPRLAND")) return Escritorio.Hyprland;
                if (e.Contains("SWAY")) return Escritorio.Sway;
                if (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "x11" || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null)
                    return Escritorio.X11;
                return Escritorio.Otro;
            }
        }
    }

    static class Sistema
    {
        public static string Usuario => Environment.UserName;

        public static bool Existe(string comando) => Buscar(comando) != null;

        /// Ruta completa de un comando del PATH (o null)
        public static string Buscar(string comando)
        {
            if (comando.Contains('/')) return File.Exists(comando) ? comando : null;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
            // Al ejecutarse desde Sunshine o un acceso directo el PATH puede venir recortado
            foreach (var dir in path.Split(':').Concat(new[] { "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin" }).Distinct())
            {
                if (dir.Length == 0) continue;
                var f = Path.Combine(dir, comando);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        /// Entorno para los procesos hijos. Dentro de un AppImage, LD_LIBRARY_PATH y similares apuntan a
        /// las bibliotecas empaquetadas y romperían los programas del sistema: se limpian.
        static void LimpiarEntorno(ProcessStartInfo psi)
        {
            foreach (var v in new[] { "LD_LIBRARY_PATH", "LD_PRELOAD", "PYTHONHOME", "PYTHONPATH", "GDK_PIXBUF_MODULE_FILE", "GIO_MODULE_DIR", "QT_PLUGIN_PATH" })
                psi.Environment.Remove(v);
        }

        public static (int codigo, string salida) Ejecutar(string exe, IEnumerable<string> argumentos, int msMax = 120000,
                                                           Action<string> porLinea = null, CancellationToken cancelar = default)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in argumentos) psi.ArgumentList.Add(a);
            LimpiarEntorno(psi);
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception ex) { return (-2, ex.Message); }
            using (p)
            {
                p.StandardInput.Close();
                var salida = new StringBuilder();
                void Leer(string l)
                {
                    if (l == null) return;
                    lock (salida) salida.AppendLine(l);
                    porLinea?.Invoke(l);
                }
                p.OutputDataReceived += (s, e) => Leer(e.Data);
                p.ErrorDataReceived += (s, e) => Leer(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using (cancelar.Register(() => { try { p.Kill(true); } catch { } }))
                {
                    if (!p.WaitForExit(msMax)) { try { p.Kill(true); } catch { } return (-1, "Tiempo de espera agotado"); }
                    p.WaitForExit();
                }
                cancelar.ThrowIfCancellationRequested();
                lock (salida) return (p.ExitCode, salida.ToString().Trim());
            }
        }

        public static (int codigo, string salida) Ejecutar(string exe, params string[] argumentos) => Ejecutar(exe, argumentos, 120000);

        /// Lanza un programa sin esperar (navegador, etc.)
        public static void Abrir(string exe, params string[] argumentos)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in argumentos) psi.ArgumentList.Add(a);
                LimpiarEntorno(psi);
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) { Registro.Escribir($"No se pudo abrir {exe}: {ex.Message}"); }
        }

        public static void AbrirUrl(string url) => Abrir("xdg-open", url);

        /// Ejecuta un script de bash como administrador (con la ventana de contraseña del sistema, pkexec).
        /// Devuelve el código de salida; 126/127 significan que se canceló o no se dio la contraseña.
        public static Task<int> ComoAdministrador(string script, Action<string> porLinea)
        {
            return Task.Run(() =>
            {
                Directory.CreateDirectory(Rutas.Cache);
                var archivo = Path.Combine(Rutas.Cache, $"administrador-{Environment.ProcessId}.sh");
                File.WriteAllText(archivo, script);
                File.SetUnixFileMode(archivo, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                try
                {
                    var pkexec = Buscar("pkexec");
                    if (pkexec == null)
                        throw new Exception("Falta pkexec (polkit), que es lo que pide la contraseña de administrador.\n" +
                                            $"Puedes ejecutar los pasos a mano en una terminal:\n\nsudo bash {archivo}");
                    var (cod, _) = Ejecutar(pkexec, new[] { "/bin/bash", archivo }, 30 * 60 * 1000, porLinea);
                    return cod;
                }
                finally { try { File.Delete(archivo); } catch { } }
            });
        }

        /// Cita un texto para meterlo en un script de bash
        public static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
    }
}
