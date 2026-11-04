using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemotoFacil
{
    static class Tailscale
    {
        /// Donde dejamos los binarios sueltos en distros donde no se puede instalar el paquete (SteamOS)
        public const string CarpetaSuelta = "/var/lib/remotofacil/tailscale";

        public static string Cli => Sistema.Buscar("tailscale") ?? (File.Exists(CarpetaSuelta + "/tailscale") ? CarpetaSuelta + "/tailscale" : null);
        public static bool Instalado => Cli != null;

        public class Info
        {
            public string Estado;   // BackendState: Running, Stopped, NeedsLogin, NoState…
            public string Ip, Cuenta, Equipo;
            public bool Conectado => Estado == "Running" && Ip != null;
        }

        /// Estado completo (null si no está instalado o el servicio no responde)
        public static Info Estado()
        {
            var cli = Cli;
            if (cli == null) return null;
            var (cod, texto) = Sistema.Ejecutar(cli, new[] { "status", "--json" }, 15000);
            int i = texto.IndexOf('{');
            if (i < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(texto.Substring(i));
                var r = doc.RootElement;
                var info = new Info { Estado = Cadena(r, "BackendState") };
                if (r.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
                {
                    info.Equipo = Cadena(self, "HostName");
                    if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                        info.Ip = ips.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x != null && x.StartsWith("100."));
                    // La cuenta: User[UserID].LoginName
                    if (self.TryGetProperty("UserID", out var uid) && r.TryGetProperty("User", out var usuarios) &&
                        usuarios.ValueKind == JsonValueKind.Object && usuarios.TryGetProperty(uid.ToString(), out var u))
                        info.Cuenta = Cadena(u, "LoginName");
                }
                return info;
            }
            catch (Exception ex)
            {
                Registro.Escribir("No se pudo leer el estado de Tailscale: " + ex.Message);
                return null;
            }
        }

        static string Cadena(JsonElement e, string clave) =>
            e.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// IP solo si está conectado de verdad
        public static string Ip() => Estado() is Info i && i.Conectado ? i.Ip : null;

        /// "tailscale up" como usuario normal (funciona porque al instalar se le hace "operador").
        /// Avisa con la URL de inicio de sesión y termina cuando la persona ha entrado.
        public static async Task<(bool ok, string salida)> IniciarSesion(Action<string> alRecibirUrl, CancellationToken cancelar)
        {
            var cli = Cli ?? throw new Exception("Tailscale no está instalado.");
            string salida = "";
            for (int intento = 0; intento < 10; intento++)
            {
                bool urlEnviada = false;
                var (cod, texto) = await Task.Run(() => Sistema.Ejecutar(cli, new[] { "up" }, 30 * 60 * 1000, linea =>
                {
                    var m = Regex.Match(linea, @"https://login\.tailscale\.com/\S+");
                    if (m.Success && !urlEnviada) { urlEnviada = true; alRecibirUrl(m.Value); }
                }, cancelar));
                salida = texto;
                if (cod == 0) break;
                // Recién instalado, el servicio puede tardar unos segundos en arrancar
                if (texto.IndexOf("failed to connect", StringComparison.OrdinalIgnoreCase) < 0 &&
                    texto.IndexOf("tailscaled", StringComparison.OrdinalIgnoreCase) < 0) break;
                await Task.Delay(3000, cancelar);
            }
            for (int i = 0; i < 10; i++)
            {
                if (Ip() != null) return (true, salida);
                await Task.Delay(1000, cancelar);
            }
            if (salida.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || salida.Contains("operator", StringComparison.OrdinalIgnoreCase))
                salida += "\n\nTu usuario no tiene permiso para manejar Tailscale. Repite el paso de instalación.";
            return (false, salida);
        }

        public static void CerrarSesion()
        {
            if (Cli is string cli) Sistema.Ejecutar(cli, new[] { "logout" }, 30000);
        }
    }
}
