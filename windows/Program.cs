using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Windows.Forms;

namespace RemotoFacil
{
    static class Rutas
    {
        static string Une(Environment.SpecialFolder f, params string[] partes) =>
            Path.Combine(new[] { Environment.GetFolderPath(f) }.Concat(partes).ToArray());

        public static string CarpetaPrograma => Une(Environment.SpecialFolder.ProgramFiles, "RemotoFacil");
        public static string ExeInstalado => Path.Combine(CarpetaPrograma, "RemotoFacil.exe");

        // Compartido entre el asistente (admin) y Sunshine (usuario normal)
        public static string ArchivoPantallas => Une(Environment.SpecialFolder.CommonApplicationData, "RemotoFacil", "pantallas.txt");
        // Existe cuando el asistente llegó al final: a partir de ahí se abre el centro de control
        public static string MarcaConfigurado => Une(Environment.SpecialFolder.CommonApplicationData, "RemotoFacil", "configurado.txt");
        public static string CarpetaComun => Une(Environment.SpecialFolder.CommonApplicationData, "RemotoFacil");
        // Qué programas instaló RemotoFácil (los que ya estaban no se marcan al desinstalar) y cómo estaba la energía antes
        public static string ArchivoInstalados => Path.Combine(CarpetaComun, "instalados.txt");
        public static string ArchivoEnergia => Path.Combine(CarpetaComun, "energia.txt");
        public static string CarpetaTemporal => Path.Combine(Path.GetTempPath(), "RemotoFacil");

        // Por usuario: lo escribe el proceso que lanza Sunshine sin elevar
        public static string CarpetaUsuario => Une(Environment.SpecialFolder.LocalApplicationData, "RemotoFacil");
        public static string ArchivoEstado => Path.Combine(CarpetaUsuario, "estado-pantallas.bin");
        public static string ArchivoRegistro => Path.Combine(CarpetaUsuario, "registro.txt");
        public static string ArchivoCredenciales => Path.Combine(CarpetaUsuario, "sunshine.dat");

        public static string SunshineDir => Une(Environment.SpecialFolder.ProgramFiles, "Sunshine");
        public static string SunshineExe => Path.Combine(SunshineDir, "sunshine.exe");
        public static string SunshineConf => Path.Combine(SunshineDir, "config", "sunshine.conf");
        public static string TailscaleExe => Une(Environment.SpecialFolder.ProgramFiles, "Tailscale", "tailscale.exe");
        public static string TailscaleIpn => Une(Environment.SpecialFolder.ProgramFiles, "Tailscale", "tailscale-ipn.exe");
    }

    static class Registro
    {
        /// Al desinstalar se apaga, para no volver a crear la carpeta que se acaba de borrar
        public static bool Desactivado;

        public static void Escribir(string texto)
        {
            if (Desactivado) return;
            try
            {
                Directory.CreateDirectory(Rutas.CarpetaUsuario);
                var f = new FileInfo(Rutas.ArchivoRegistro);
                if (f.Exists && f.Length > 512 * 1024) f.Delete();
                File.AppendAllText(Rutas.ArchivoRegistro, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {texto}\r\n");
            }
            catch { }
        }
    }

    static class Iconos
    {
        static System.Drawing.Icon app;

        /// El icono va incrustado en el exe; si no se encuentra, el del propio ejecutable.
        public static System.Drawing.Icon App
        {
            get
            {
                if (app != null) return app;
                try
                {
                    using (var s = typeof(Iconos).Assembly.GetManifestResourceStream("icono.ico"))
                        if (s != null) return app = new System.Drawing.Icon(s);
                }
                catch { }
                try { return app = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
            }
        }

        /// El logo en PNG (96 px) para la cabecera del centro de control
        public static System.Drawing.Image Logo()
        {
            try
            {
                using (var s = typeof(Iconos).Assembly.GetManifestResourceStream("logo.png"))
                using (var b = new System.Drawing.Bitmap(s))
                    return new System.Drawing.Bitmap(b);   // copia: Bitmap necesita su stream abierto mientras viva
            }
            catch { return null; }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Modos que usa Sunshine al empezar/terminar la sesión. Siempre devuelven 0:
            // si un comando previo falla, Sunshine cancela la conexión, y eso es peor que no apagar una pantalla.
            if (args.Contains("--apagar-pantallas"))
            {
                try { Pantallas.ApagarSegunConfiguracion(); }
                catch (Exception e) { Registro.Escribir("Error al apagar pantallas: " + e); }
                return 0;
            }
            if (args.Contains("--restaurar-pantallas"))
            {
                try { Pantallas.Restaurar(forzar: args.Contains("--forzar")); }
                catch (Exception e) { Registro.Escribir("Error al restaurar pantallas: " + e); }
                return 0;
            }

            if (!EsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args.Select(Acciones.Citar)))
                    { Verb = "runas", UseShellExecute = true });
                }
                catch (Win32Exception)
                {
                    MessageBox.Show("RemotoFácil necesita permisos de administrador para instalar los programas.\n\n" +
                                    "Vuelve a abrirlo y pulsa \"Sí\" cuando Windows lo pregunte.",
                                    "RemotoFácil", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            // Sunshine usa un certificado autofirmado: lo aceptamos solo para nuestra propia máquina
            ServicePointManager.ServerCertificateValidationCallback = (s, cert, cadena, errores) =>
                errores == System.Net.Security.SslPolicyErrors.None || (s is HttpWebRequest r && r.RequestUri.IsLoopback);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--desinstalar"))   // desde "Aplicaciones" de Windows
                Application.Run(new Asistente(new Estado(), new PaginaDesinstalar()));
            else if (!args.Contains("--asistente") && Acciones.YaConfigurado)
                Application.Run(new CentroControl());
            else
                Application.Run(new Asistente());
            return 0;
        }

        static bool EsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
