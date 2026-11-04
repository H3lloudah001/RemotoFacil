using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemotoFacil
{
    static class Sunshine
    {
        public const string IdFlatpak = "dev.lizardbyte.app.Sunshine";
        public const string UrlWeb = "https://localhost:47990";
        const string Marca = "RemotoFacil";

        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string Nativo => Sistema.Buscar("sunshine");
        public static bool EsFlatpak => Nativo == null && Sistema.Existe("flatpak") &&
                                        Sistema.Ejecutar("flatpak", new[] { "info", IdFlatpak }, 15000).codigo == 0;
        public static bool Instalado => Nativo != null || EsFlatpak;

        public static string CarpetaConfig => Nativo == null && Directory.Exists(Path.Combine(Home, ".var/app", IdFlatpak))
            ? Path.Combine(Home, ".var/app", IdFlatpak, "config/sunshine")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is string x && x.Length > 0 ? x : Path.Combine(Home, ".config"), "sunshine");

        public static string ArchivoConfig => Path.Combine(CarpetaConfig, "sunshine.conf");

        /// Ejecuta el binario de Sunshine (nativo o dentro del Flatpak)
        static (int, string) EjecutarSunshine(int msMax, params string[] args)
        {
            if (Nativo is string n) return Sistema.Ejecutar(n, args, msMax);
            return Sistema.Ejecutar("flatpak", new[] { "run", "--command=sunshine", IdFlatpak }.Concat(args), msMax);
        }

        public static void PonerCredenciales(string usuario, string clave)
        {
            // Sin ruta de configuración: usa la de siempre del usuario. (Si se pasara, tendría que ir ANTES de --creds)
            var (cod, salida) = EjecutarSunshine(60000, "--creds", usuario, clave);
            if (cod != 0) throw new Exception("Sunshine no aceptó el usuario y la contraseña:\n" + salida);
        }

        // ---------- Servicio (de usuario: Sunshine necesita la sesión gráfica) ----------

        /// Nombre del servicio de systemd de usuario, que cambia según la versión y el paquete
        public static string Servicio()
        {
            var (_, salida) = Sistema.Ejecutar("systemctl", new[] { "--user", "list-unit-files", "--no-legend", "--plain", "--type=service" }, 15000);
            var unidades = salida.Split('\n').Select(l => l.Trim().Split(' ')[0])
                                 .Where(u => u.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) >= 0 && u.EndsWith(".service")).ToList();
            return unidades.FirstOrDefault(u => u.StartsWith("app-dev.lizardbyte")) ?? unidades.FirstOrDefault(u => u == "sunshine.service") ?? unidades.FirstOrDefault();
        }

        public static bool Activo()
        {
            var s = Servicio();
            return s != null && Sistema.Ejecutar("systemctl", new[] { "--user", "is-active", s }, 10000).salida.Trim() == "active";
        }

        /// Servicio propio, solo si el paquete no trae ninguno (algunas versiones del Flatpak)
        public static string ServicioPropio => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is string x && x.Length > 0 ? x : Path.Combine(Home, ".config"),
            "systemd", "user", "sunshine.service");

        public static void ActivarServicio()
        {
            Sistema.Ejecutar("systemctl", new[] { "--user", "daemon-reload" }, 30000);
            if (Servicio() == null)
            {
                var inicio = Nativo != null ? $"\"{Nativo}\"" : $"{Sistema.Buscar("flatpak") ?? "/usr/bin/flatpak"} run {IdFlatpak}";
                Directory.CreateDirectory(Path.GetDirectoryName(ServicioPropio));
                File.WriteAllText(ServicioPropio,
                    "# Creado por RemotoFacil\n" +
                    "[Unit]\nDescription=Sunshine\nAfter=graphical-session.target\nPartOf=graphical-session.target\n\n" +
                    $"[Service]\nExecStart={inicio}\nRestart=on-failure\nRestartSec=5\n\n" +
                    "[Install]\nWantedBy=graphical-session.target\n");
                Sistema.Ejecutar("systemctl", new[] { "--user", "daemon-reload" }, 30000);
            }
            var s = Servicio() ?? throw new Exception("No se encontró el servicio de Sunshine.");
            var (cod, salida) = Sistema.Ejecutar("systemctl", new[] { "--user", "enable", "--now", s }, 60000);
            if (cod != 0) throw new Exception("No se pudo activar Sunshine: " + salida);
        }

        public static void Reiniciar()
        {
            var s = Servicio() ?? throw new Exception("No se encontró el servicio de Sunshine.");
            var (cod, salida) = Sistema.Ejecutar("systemctl", new[] { "--user", "restart", s }, 60000);
            if (cod != 0) throw new Exception("No se pudo reiniciar Sunshine: " + salida);
            EsperarPanel();
        }

        /// Espera a que el panel web responda (un 401 también vale: está vivo)
        public static void EsperarPanel()
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using var r = Cliente.GetAsync(UrlWeb + "/api/pin").GetAwaiter().GetResult();
                    return;
                }
                catch { }
                Thread.Sleep(1000);
            }
            throw new Exception("Sunshine no responde después de reiniciarlo.");
        }

        // ---------- Comandos al empezar/terminar la sesión (apagar pantallas) ----------

        /// Añade o quita los comandos de RemotoFácil en global_prep_cmd sin tocar los que ya hubiera
        public static void ConfigurarPantallas(bool activar)
        {
            var conf = ArchivoConfig;
            Directory.CreateDirectory(Path.GetDirectoryName(conf));
            var lineas = File.Exists(conf) ? File.ReadAllLines(conf).ToList() : new List<string>();
            var copia = conf + ".antes-de-remotofacil";
            if (File.Exists(conf) && !File.Exists(copia)) File.Copy(conf, copia);

            int idx = lineas.FindIndex(l => Regex.IsMatch(l, @"^\s*global_prep_cmd\s*="));
            var lista = new JsonArray();
            if (idx >= 0)
            {
                try { lista = JsonNode.Parse(lineas[idx].Substring(lineas[idx].IndexOf('=') + 1).Trim()) as JsonArray ?? lista; }
                catch { /* ilegible: se sustituye (queda la copia) */ }
            }
            foreach (var n in lista.ToList())
                if ((n?["do"]?.GetValue<string>() ?? "").Contains(Marca, StringComparison.OrdinalIgnoreCase)) lista.Remove(n);
            if (activar)
            {
                var exe = Rutas.EjecutableInstalado;
                lista.Add(new JsonObject
                {
                    ["do"] = $"\"{exe}\" --apagar-pantallas",
                    ["undo"] = $"\"{exe}\" --restaurar-pantallas",
                    ["elevated"] = false,
                });
            }
            var nueva = "global_prep_cmd = " + lista.ToJsonString();
            if (lista.Count == 0) { if (idx >= 0) lineas.RemoveAt(idx); }
            else if (idx >= 0) lineas[idx] = nueva;
            else lineas.Add(nueva);
            File.WriteAllLines(conf, lineas);
        }

        // ---------- API del panel web ----------

        // Certificado autofirmado: se acepta solo para nuestra propia máquina
        static readonly HttpClient Cliente = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, cert, cadena, errores) =>
                errores == System.Net.Security.SslPolicyErrors.None || (msg.RequestUri?.IsLoopback ?? false),
        }) { Timeout = TimeSpan.FromSeconds(90) };

        public class CredencialesIncorrectas : Exception
        {
            public CredencialesIncorrectas() : base("El usuario o la contraseña de Sunshine no son correctos.") { }
        }

        static JsonDocument Llamar(HttpMethod metodo, string ruta, string usuario, string clave, Dictionary<string, string> cuerpo = null)
        {
            using var req = new HttpRequestMessage(metodo, UrlWeb + ruta);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(usuario + ":" + clave)));
            if (cuerpo != null) req.Content = new StringContent(JsonSerializer.Serialize(cuerpo, ContextoJson.Default.DictionaryStringString), Encoding.UTF8, "application/json");
            using var resp = Cliente.Send(req);
            var texto = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new CredencialesIncorrectas();
            if (!resp.IsSuccessStatusCode) throw new Exception($"Sunshine respondió con un error ({(int)resp.StatusCode}). {texto}");
            return JsonDocument.Parse(texto.Length == 0 ? "{}" : texto);
        }

        static bool StatusOk(JsonDocument d) =>
            d.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.True;

        static string Cad(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public class Solicitud { public string Id, Nombre, Direccion; }
        public class Dispositivo { public string Nombre, Uuid; }

        public static List<Solicitud> SolicitudesPendientes(string usuario, string clave)
        {
            using var d = Llamar(HttpMethod.Get, "/api/pin", usuario, clave);
            if (!d.RootElement.TryGetProperty("pairings", out var p) || p.ValueKind != JsonValueKind.Array) return new List<Solicitud>();
            return p.EnumerateArray().Select(x => new Solicitud { Id = Cad(x, "id"), Nombre = Cad(x, "name"), Direccion = Cad(x, "address") })
                    .Where(x => !string.IsNullOrEmpty(x.Id)).ToList();
        }

        public static bool Emparejar(string usuario, string clave, string pin, string nombre, string idSolicitud = null)
        {
            for (int i = 0; i < 10 && idSolicitud == null; i++)
            {
                idSolicitud = SolicitudesPendientes(usuario, clave).LastOrDefault()?.Id;
                if (idSolicitud == null) Thread.Sleep(1000);
            }
            if (idSolicitud == null)
                throw new Exception("Sunshine no ha recibido ninguna solicitud del dispositivo.\n\n" +
                                    "Comprueba que Tailscale está activado en él, y que en Moonlight has tocado el PC y te muestra el PIN.");
            using var d = Llamar(HttpMethod.Post, "/api/pin", usuario, clave,
                                 new Dictionary<string, string> { ["pairing_id"] = idSolicitud, ["pin"] = pin, ["name"] = nombre });
            return StatusOk(d);
        }

        public static List<Dispositivo> Dispositivos(string usuario, string clave)
        {
            using var d = Llamar(HttpMethod.Get, "/api/clients/list", usuario, clave);
            if (!d.RootElement.TryGetProperty("named_certs", out var c) || c.ValueKind != JsonValueKind.Array) return new List<Dispositivo>();
            return c.EnumerateArray().Select(x => new Dispositivo { Nombre = Cad(x, "name"), Uuid = Cad(x, "uuid") })
                    .Where(x => !string.IsNullOrEmpty(x.Uuid)).OrderBy(x => x.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static bool QuitarDispositivo(string usuario, string clave, string uuid)
        {
            using var d = Llamar(HttpMethod.Post, "/api/clients/unpair", usuario, clave, new Dictionary<string, string> { ["uuid"] = uuid });
            return StatusOk(d);
        }

        public static bool ComprobarCredenciales(string usuario, string clave)
        {
            try { using var d = Llamar(HttpMethod.Get, "/api/pin", usuario, clave); return true; }
            catch (CredencialesIncorrectas) { return false; }
        }

        // ---------- Contraseña guardada ----------
        // En Linux no hay un almacén de secretos que exista en todas las distros: se guarda en un archivo
        // que solo puede leer el propio usuario (igual que hace Sunshine con su configuración).

        public static void GuardarCredenciales(string usuario, string clave)
        {
            try
            {
                Directory.CreateDirectory(Rutas.Config);
                File.WriteAllText(Rutas.Credenciales, JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["usuario"] = usuario, ["clave"] = clave }, ContextoJson.Default.DictionaryStringString));
                File.SetUnixFileMode(Rutas.Credenciales, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) { Registro.Escribir("No se pudo guardar la contraseña de Sunshine: " + ex.Message); }
        }

        public static (string usuario, string clave)? LeerCredenciales()
        {
            try
            {
                if (!File.Exists(Rutas.Credenciales)) return null;
                var d = JsonSerializer.Deserialize(File.ReadAllText(Rutas.Credenciales), ContextoJson.Default.DictionaryStringString);
                return d != null && d.TryGetValue("usuario", out var u) && d.TryGetValue("clave", out var c) ? (u, c) : null;
            }
            catch { return null; }
        }

        public static void BorrarCredenciales()
        {
            try { File.Delete(Rutas.Credenciales); } catch { }
        }
    }

    /// Serialización sin reflexión (para que funcione aunque el ejecutable se recorte al empaquetar)
    [System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
    partial class ContextoJson : System.Text.Json.Serialization.JsonSerializerContext { }
}
