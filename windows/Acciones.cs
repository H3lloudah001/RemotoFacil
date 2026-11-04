using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RemotoFacil
{
    static class Acciones
    {
        const string ServicioSunshine = "SunshineService";
        const string UrlSunshineWeb = "https://127.0.0.1:47990";
        const string MarcaComando = "RemotoFacil";

        // ---------- Utilidades ----------

        [DllImport("kernel32.dll")]
        static extern bool IsWow64Process2(IntPtr proceso, out ushort maquinaProceso, out ushort maquinaNativa);

        public static bool EsArm64()
        {
            try
            {
                if (IsWow64Process2(Process.GetCurrentProcess().Handle, out _, out ushort nativa))
                    return nativa == 0xAA64;
            }
            catch { }
            return (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant() == "ARM64";
        }

        public static string Citar(string arg)
        {
            // Reglas de CommandLineToArgvW: las barras antes de una comilla se duplican
            var sb = new StringBuilder("\"");
            int barras = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { barras++; continue; }
                if (c == '"') sb.Append('\\', barras * 2 + 1);
                else sb.Append('\\', barras);
                barras = 0;
                sb.Append(c);
            }
            sb.Append('\\', barras * 2).Append('"');
            return sb.ToString();
        }

        public static (int codigo, string salida) Ejecutar(string exe, string argumentos, int msMax = 120000)
        {
            var psi = new ProcessStartInfo(exe, argumentos)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                var salida = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (salida) salida.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (salida) salida.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(msMax)) { try { p.Kill(); } catch { } return (-1, "Tiempo de espera agotado"); }
                p.WaitForExit();
                lock (salida) return (p.ExitCode, salida.ToString().Trim());
            }
        }

        public static void AbrirEnNavegador(string url)
        {
            // A través de explorer para que el navegador no se abra como administrador
            try { Process.Start("explorer.exe", Citar(url)); }
            catch { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        }

        // ---------- Instalación ----------

        public static bool TailscaleInstalado => File.Exists(Rutas.TailscaleExe);
        public static bool SunshineInstalado => File.Exists(Rutas.SunshineExe);

        /// Opciones del MSI de Tailscale: siempre conectado aunque nadie inicie sesión en Windows, y sin abrir su
        /// propia ventana de bienvenida al instalarse (el inicio de sesión se hace desde RemotoFácil).
        public const string OpcionesMsiTailscale = "TS_UNATTENDEDMODE=always TS_NOLAUNCH=1 TS_ONBOARDING_FLOW=hide";

        static string AccesoMenuInicio => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "RemotoFácil.lnk");

        /// Ya se completó el asistente alguna vez (las versiones anteriores no dejaban marca, pero sí el acceso del menú Inicio)
        public static bool YaConfigurado =>
            TailscaleInstalado && SunshineInstalado && (File.Exists(Rutas.MarcaConfigurado) || File.Exists(AccesoMenuInicio));

        public static void MarcarConfigurado()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Rutas.MarcaConfigurado));
            File.WriteAllText(Rutas.MarcaConfigurado, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        public static string UrlTailscale => EsArm64()
            ? "https://pkgs.tailscale.com/stable/tailscale-setup-latest-arm64.msi"
            : "https://pkgs.tailscale.com/stable/tailscale-setup-latest-amd64.msi";

        public static string UrlSunshine => EsArm64()
            ? "https://github.com/LizardByte/Sunshine/releases/latest/download/Sunshine-Windows-ARM64-installer.msi"
            : "https://github.com/LizardByte/Sunshine/releases/latest/download/Sunshine-Windows-AMD64-installer.msi";

        public static async Task<string> Descargar(string url, string nombre, IProgress<int> progreso)
        {
            var carpeta = Rutas.CarpetaTemporal;
            Directory.CreateDirectory(carpeta);
            var destino = Path.Combine(carpeta, nombre);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "RemotoFacil/1.0";
                wc.DownloadProgressChanged += (s, e) => progreso?.Report(e.ProgressPercentage);
                await wc.DownloadFileTaskAsync(new Uri(url), destino);
            }
            return destino;
        }

        public static void InstalarMsi(string msi, string propiedades = "")
        {
            var log = msi + ".log";
            var (codigo, _) = Ejecutar("msiexec.exe", $"/i {Citar(msi)} /qn /norestart /l*v {Citar(log)} {propiedades}", 15 * 60 * 1000);
            if (codigo == 0 || codigo == 3010) return; // 3010 = instalado, pide reiniciar
            if (codigo == 1618)
                throw new Exception("Windows está instalando otra cosa ahora mismo (por ejemplo, actualizaciones). Espera un poco y pulsa Reintentar.");
            throw new Exception($"El instalador terminó con el código {codigo}. Registro: {log}");
        }

        public static void CopiarseAProgramFiles()
        {
            var origen = Path.GetFullPath(Application.ExecutablePath);
            var destino = Path.GetFullPath(Rutas.ExeInstalado);
            if (!string.Equals(origen, destino, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Rutas.CarpetaPrograma);
                File.Copy(origen, destino, true);
            }
            RegistrarEnAplicaciones();
        }

        const string ClaveDesinstalar = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RemotoFacil";

        /// Aparece en Configuración → Aplicaciones, con su botón de desinstalar
        public static void RegistrarEnAplicaciones()
        {
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hklm.CreateSubKey(ClaveDesinstalar))
                {
                    var exe = Rutas.ExeInstalado;
                    k.SetValue("DisplayName", "RemotoFácil");
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("DisplayVersion", typeof(Acciones).Assembly.GetName().Version.ToString(3));
                    k.SetValue("Publisher", "RemotoFácil");
                    k.SetValue("InstallLocation", Rutas.CarpetaPrograma);
                    k.SetValue("UninstallString", $"\"{exe}\" --desinstalar");
                    k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    if (File.Exists(exe))
                        k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Registro.Escribir("No se pudo registrar en Aplicaciones: " + ex.Message); }
        }

        public static void QuitarDeAplicaciones()
        {
            using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                hklm.DeleteSubKeyTree(ClaveDesinstalar, false);
        }

        /// Apunta que RemotoFácil instaló este programa (para proponer quitarlo al desinstalar)
        public static void AnotarInstalado(string programa)
        {
            try
            {
                Directory.CreateDirectory(Rutas.CarpetaComun);
                var ya = File.Exists(Rutas.ArchivoInstalados) ? File.ReadAllLines(Rutas.ArchivoInstalados) : new string[0];
                if (!ya.Contains(programa)) File.AppendAllLines(Rutas.ArchivoInstalados, new[] { programa });
            }
            catch (Exception ex) { Registro.Escribir("No se pudo anotar la instalación: " + ex.Message); }
        }

        /// true si lo instaló RemotoFácil; null si no se sabe (instalaciones de versiones anteriores)
        public static bool? LoInstaloRemotoFacil(string programa)
        {
            try { return File.Exists(Rutas.ArchivoInstalados) ? File.ReadAllLines(Rutas.ArchivoInstalados).Contains(programa) : (bool?)null; }
            catch { return null; }
        }

        public static void PrepararServicioSunshine()
        {
            Ejecutar("sc.exe", $"config {ServicioSunshine} start= auto");
            // El instalador de Sunshine ya añade reglas, pero las nuestras cubren cualquier perfil de red (Tailscale incluido)
            Ejecutar("netsh.exe", "advfirewall firewall delete rule name=\"RemotoFacil Sunshine\"");
            Ejecutar("netsh.exe", $"advfirewall firewall add rule name=\"RemotoFacil Sunshine\" dir=in action=allow program={Citar(Rutas.SunshineExe)} enable=yes profile=any");
        }

        public static string AccesoEscritorio => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Restaurar pantallas.lnk");

        /// Acceso al centro de control en el escritorio público (lo ven todos los usuarios del PC)
        public static string AccesoEscritorioApp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "RemotoFácil.lnk");

        public static void AccesoEnEscritorio(bool crear)
        {
            if (crear) CrearAcceso(AccesoEscritorioApp, Rutas.ExeInstalado, "", "Centro de control del acceso remoto a este PC");
            else if (File.Exists(AccesoEscritorioApp)) File.Delete(AccesoEscritorioApp);
        }

        public static void BorrarAccesosDirectos()
        {
            foreach (var lnk in new[] { AccesoMenuInicio, AccesoEscritorio, AccesoEscritorioApp })
                if (File.Exists(lnk)) File.Delete(lnk);
        }

        public static void BorrarReglaCortafuegos() =>
            Ejecutar("netsh.exe", "advfirewall firewall delete rule name=\"RemotoFacil Sunshine\"");

        public static void CrearAccesosDirectos(bool restaurarPantallas)
        {
            CrearAcceso(AccesoMenuInicio, Rutas.ExeInstalado, "", "Centro de control del acceso remoto a este PC");

            var escritorio = AccesoEscritorio;
            if (restaurarPantallas)
                CrearAcceso(escritorio, Rutas.ExeInstalado, "--restaurar-pantallas --forzar", "Vuelve a encender las pantallas que apagó RemotoFácil");
            else if (File.Exists(escritorio))
                File.Delete(escritorio);
        }

        static void CrearAcceso(string lnk, string destino, string argumentos, string descripcion)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                dynamic acceso = shell.CreateShortcut(lnk);
                acceso.TargetPath = destino;
                acceso.Arguments = argumentos;
                acceso.Description = descripcion;
                acceso.WorkingDirectory = Path.GetDirectoryName(destino);
                acceso.Save();
                Marshal.FinalReleaseComObject(acceso);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        public static void NoSuspenderEnchufado()
        {
            GuardarEnergiaOriginal();
            Ejecutar("powercfg.exe", "/change standby-timeout-ac 0");
            Ejecutar("powercfg.exe", "/change hibernate-timeout-ac 0");
        }

        static readonly string[] AjustesEnergia = { "STANDBYIDLE", "HIBERNATEIDLE" };

        /// Apunta (solo la primera vez) tras cuántos segundos se suspendía el PC enchufado, para devolverlo al desinstalar
        static void GuardarEnergiaOriginal()
        {
            if (File.Exists(Rutas.ArchivoEnergia)) return;
            try
            {
                var lineas = new List<string>();
                foreach (var ajuste in AjustesEnergia)
                {
                    // La salida está traducida, pero los valores siempre van en hexadecimal y los dos últimos son
                    // "corriente alterna" (enchufado) y "corriente continua" (batería), en ese orden
                    var (cod, salida) = Ejecutar("powercfg.exe", $"/query SCHEME_CURRENT SUB_SLEEP {ajuste}", 15000);
                    var valores = Regex.Matches(salida, @"0x[0-9a-fA-F]+").Cast<Match>().Select(m => m.Value).ToList();
                    if (cod != 0 || valores.Count < 2) return;
                    lineas.Add(ajuste + "=" + Convert.ToUInt32(valores[valores.Count - 2], 16));
                }
                Directory.CreateDirectory(Rutas.CarpetaComun);
                File.WriteAllLines(Rutas.ArchivoEnergia, lineas);
            }
            catch (Exception ex) { Registro.Escribir("No se pudo guardar la configuración de energía: " + ex.Message); }
        }

        /// Devuelve la suspensión a como estaba. false si no había nada apuntado.
        public static bool RestaurarEnergia()
        {
            if (!File.Exists(Rutas.ArchivoEnergia)) return false;
            foreach (var l in File.ReadAllLines(Rutas.ArchivoEnergia))
            {
                var partes = l.Split('=');
                if (partes.Length == 2 && AjustesEnergia.Contains(partes[0]) && uint.TryParse(partes[1], out uint seg))
                    Ejecutar("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT SUB_SLEEP {partes[0]} {seg}");
            }
            Ejecutar("powercfg.exe", "/setactive SCHEME_CURRENT");
            return true;
        }

        // ---------- Tailscale ----------

        /// IP del PC en Tailscale, solo si está conectado de verdad. Con la sesión iniciada pero
        /// desactivado en la bandeja, "tailscale ip" sigue dando la IP aunque nadie pueda llegar al PC.
        public static string IpTailscale()
        {
            if (!TailscaleInstalado) return null;
            var (cod, json) = Ejecutar(Rutas.TailscaleExe, "status --json", 15000);
            if (cod != 0 || !Regex.IsMatch(json, "\"BackendState\"\\s*:\\s*\"Running\"")) return null;
            var (codigo, salida) = Ejecutar(Rutas.TailscaleExe, "ip -4", 15000);
            if (codigo != 0) return null;
            var linea = salida.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("100."));
            return string.IsNullOrEmpty(linea) ? null : linea;
        }

        public class InfoTailscale
        {
            public string Estado;   // BackendState: Running, Stopped, NeedsLogin, NoState…
            public string Ip, Cuenta, Equipo;
            public bool Conectado => Estado == "Running" && Ip != null;
        }

        /// Estado completo de Tailscale para el centro de control (null si no está instalado o no responde)
        public static InfoTailscale EstadoTailscale()
        {
            if (!TailscaleInstalado) return null;
            var (cod, texto) = Ejecutar(Rutas.TailscaleExe, "status --json", 15000);
            if (cod != 0 && texto.IndexOf('{') < 0) return null;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var d = json.Deserialize<Dictionary<string, object>>(texto.Substring(texto.IndexOf('{')));
                var info = new InfoTailscale { Estado = d.TryGetValue("BackendState", out var e) ? e as string : null };
                if (d.TryGetValue("Self", out var so) && so is Dictionary<string, object> self)
                {
                    info.Equipo = self.TryGetValue("HostName", out var h) ? h as string : null;
                    if (self.TryGetValue("TailscaleIPs", out var ips) && ips is System.Collections.ArrayList lista)
                        info.Ip = lista.OfType<string>().FirstOrDefault(x => x.StartsWith("100."));
                    // La cuenta: User[UserID].LoginName
                    if (self.TryGetValue("UserID", out var uid) && d.TryGetValue("User", out var uo) &&
                        uo is Dictionary<string, object> usuarios &&
                        usuarios.TryGetValue(Convert.ToString(uid), out var u) && u is Dictionary<string, object> usuario)
                        info.Cuenta = usuario.TryGetValue("LoginName", out var l) ? l as string : null;
                }
                return info;
            }
            catch (Exception ex)
            {
                Registro.Escribir("No se pudo leer el estado de Tailscale: " + ex.Message);
                return null;
            }
        }

        /// Abre el icono de Tailscale en la bandeja (sin permisos de administrador), si no está ya abierto
        public static void AbrirBandejaTailscale()
        {
            if (!File.Exists(Rutas.TailscaleIpn) || Process.GetProcessesByName("tailscale-ipn").Length > 0) return;
            try { Process.Start("explorer.exe", Citar(Rutas.TailscaleIpn)); } catch { }
        }

        /// Lanza "tailscale up" y avisa con la URL de inicio de sesión. Termina cuando el usuario ha iniciado sesión.
        public static async Task<(bool ok, string salida)> IniciarSesionTailscale(Action<string> alRecibirUrl, CancellationToken cancelar)
        {
            string ultimaSalida = "";
            for (int intento = 0; intento < 10; intento++)
            {
                var salida = new StringBuilder();
                bool urlEnviada = false;
                var psi = new ProcessStartInfo(Rutas.TailscaleExe, "up --unattended")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    DataReceivedEventHandler leer = (s, e) =>
                    {
                        if (e.Data == null) return;
                        lock (salida) salida.AppendLine(e.Data);
                        var m = Regex.Match(e.Data, @"https://login\.tailscale\.com/\S+");
                        if (m.Success && !urlEnviada) { urlEnviada = true; alRecibirUrl(m.Value); }
                    };
                    p.OutputDataReceived += leer;
                    p.ErrorDataReceived += leer;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (cancelar.Register(() => { try { p.Kill(); } catch { } }))
                        await Task.Run(() => p.WaitForExit());
                    cancelar.ThrowIfCancellationRequested();
                    lock (salida) ultimaSalida = salida.ToString().Trim();
                    if (p.ExitCode == 0) break;
                }
                // Recién instalado, el servicio de Tailscale puede tardar unos segundos en arrancar
                if (ultimaSalida.IndexOf("failed to connect", StringComparison.OrdinalIgnoreCase) < 0 &&
                    ultimaSalida.IndexOf("tailscaled", StringComparison.OrdinalIgnoreCase) < 0) break;
                await Task.Delay(3000, cancelar);
            }

            // Por si la cuenta ya estaba configurada con otras opciones y "up" se quejó: comprobar la IP directamente
            for (int i = 0; i < 10; i++)
            {
                if (IpTailscale() != null)
                {
                    Ejecutar(Rutas.TailscaleExe, "set --unattended=true", 15000);
                    return (true, ultimaSalida);
                }
                await Task.Delay(1000, cancelar);
            }
            return (false, ultimaSalida);
        }

        // ---------- Sunshine ----------

        public static void CredencialesSunshine(string usuario, string clave)
        {
            var (codigo, salida) = Ejecutar(Rutas.SunshineExe, $"--creds {Citar(usuario)} {Citar(clave)}", 60000);
            if (codigo != 0) throw new Exception("Sunshine no aceptó el usuario y la contraseña:\n" + salida);
        }

        /// Añade (o quita) los comandos de RemotoFácil en global_prep_cmd sin tocar los que ya tuviera el usuario.
        public static void ConfigurarPantallasEnSunshine(bool activar)
        {
            var lineas = LeerConfSunshine();
            int idx = lineas.FindIndex(l => Regex.IsMatch(l, @"^\s*global_prep_cmd\s*="));
            var json = new JavaScriptSerializer();
            var lista = new List<Dictionary<string, object>>();
            if (idx >= 0)
            {
                var valor = lineas[idx].Substring(lineas[idx].IndexOf('=') + 1).Trim();
                try { lista = json.Deserialize<List<Dictionary<string, object>>>(valor) ?? lista; }
                catch { /* valor ilegible: se sustituye (queda la copia de seguridad) */ }
            }
            lista.RemoveAll(d => d.TryGetValue("do", out var v) && (v as string ?? "").IndexOf(MarcaComando, StringComparison.OrdinalIgnoreCase) >= 0);
            if (activar)
            {
                lista.Add(new Dictionary<string, object>
                {
                    ["do"] = $"\"{Rutas.ExeInstalado}\" --apagar-pantallas",
                    ["undo"] = $"\"{Rutas.ExeInstalado}\" --restaurar-pantallas",
                    ["elevated"] = false,
                });
            }

            var nueva = "global_prep_cmd = " + json.Serialize(lista);
            if (lista.Count == 0) { if (idx >= 0) lineas.RemoveAt(idx); }
            else if (idx >= 0) lineas[idx] = nueva;
            else lineas.Add(nueva);
            File.WriteAllLines(Rutas.SunshineConf, lineas, new UTF8Encoding(false));
        }

        /// Líneas de sunshine.conf (la primera vez que RemotoFácil lo toca, guarda una copia de cómo estaba)
        static List<string> LeerConfSunshine()
        {
            var conf = Rutas.SunshineConf;
            Directory.CreateDirectory(Path.GetDirectoryName(conf));
            var lineas = File.Exists(conf) ? File.ReadAllLines(conf).ToList() : new List<string>();
            var copia = conf + ".antes-de-remotofacil";
            if (File.Exists(conf) && !File.Exists(copia)) File.Copy(conf, copia);
            return lineas;
        }

        /// Valor de una opción de sunshine.conf, o null si no está puesta
        public static string OpcionSunshine(string clave)
        {
            try
            {
                if (!File.Exists(Rutas.SunshineConf)) return null;
                var patron = new Regex(@"^\s*" + Regex.Escape(clave) + @"\s*=(.*)$");
                foreach (var l in File.ReadAllLines(Rutas.SunshineConf))
                {
                    var m = patron.Match(l);
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            catch { }
            return null;
        }

        static void PonerOpcionSunshine(string clave, string valor)
        {
            var lineas = LeerConfSunshine();
            int idx = lineas.FindIndex(l => Regex.IsMatch(l, @"^\s*" + Regex.Escape(clave) + @"\s*="));
            var nueva = clave + " = " + valor;
            if (idx >= 0) lineas[idx] = nueva; else lineas.Add(nueva);
            File.WriteAllLines(Rutas.SunshineConf, lineas, new UTF8Encoding(false));
        }

        // ---------- Mandos ----------
        // Las versiones actuales de Sunshine para Windows ya no instalan ningún driver de mandos. Sin uno,
        // Moonlight se conecta bien pero el PC no ve ningún mando. Hay dos: ViGEmBus (gratis, solo emula
        // mandos de Xbox 360 y DualShock 4) y el Virtual HID Driver de LizardByte (de pago). RemotoFácil
        // instala ViGEmBus y le dice a Sunshine que use solo ese (gamepad_driver = vigembus), porque si la
        // opción no está puesta Sunshine sigue pidiendo que se elija y anunciando el driver de pago.

        // Versión fija con su huella: ViGEmBus ya no se actualiza (proyecto terminado en 2023)
        const string UrlViGEm = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
        const string Sha256ViGEm = "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a";
        static readonly Version ViGEmMinima = new Version(1, 17);   // la mínima que acepta Sunshine

        /// Versión del driver ViGEmBus instalado, o null si no está
        public static Version VersionViGEm()
        {
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus"))
                {
                    if (k == null) return null;
                    var imagen = k.GetValue("ImagePath", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                    // Suele ser "\SystemRoot\System32\drivers\ViGEmBus.sys" o "System32\drivers\ViGEmBus.sys"
                    imagen = imagen.Replace("\\??\\", "");
                    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (imagen.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) imagen = Path.Combine(windows, imagen.Substring(12));
                    else if (imagen.Length > 0 && !Path.IsPathRooted(imagen)) imagen = Path.Combine(windows, imagen);
                    imagen = Environment.ExpandEnvironmentVariables(imagen);
                    if (imagen.Length == 0 || !File.Exists(imagen)) imagen = Path.Combine(windows, "System32", "drivers", "ViGEmBus.sys");
                    if (!File.Exists(imagen)) return null;
                    var v = FileVersionInfo.GetVersionInfo(imagen);
                    return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
                }
            }
            catch (Exception ex)
            {
                Registro.Escribir("No se pudo comprobar ViGEmBus: " + ex.Message);
                return null;
            }
        }

        public static bool ViGEmInstalado => VersionViGEm() is Version v && v >= ViGEmMinima;

        /// Los mandos funcionarán: hay driver y Sunshine sabe cuál usar
        public static bool MandosListos => ViGEmInstalado && !string.IsNullOrEmpty(OpcionSunshine("gamepad_driver"));

        /// Instala ViGEmBus si falta y elige el driver en Sunshine. Después hay que reiniciar Sunshine,
        /// que solo busca el driver al arrancar. Devuelve true si Windows pide reiniciar el PC.
        public static async Task<bool> PrepararMandos(IProgress<int> progreso, Action<string> log)
        {
            bool reiniciarPc = false;
            var antes = VersionViGEm();
            if (antes != null && antes >= ViGEmMinima) log($"El driver de mandos (ViGEmBus {antes}) ya estaba instalado.");
            else
            {
                if (antes != null) log($"ViGEmBus {antes} es demasiado antiguo para Sunshine; se actualiza.");
                var exe = await Descargar(UrlViGEm, "vigembus.exe", progreso);
                string huella;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(exe))
                    huella = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (huella != Sha256ViGEm)
                {
                    File.Delete(exe);
                    throw new Exception("El instalador del driver de mandos descargado no es el esperado (puede que la descarga se cortara).");
                }
                log("Driver de mandos descargado.");
                // Es un instalador de Advanced Installer: /exenoui quita su ventana y el resto va al MSI de dentro
                var registroMsi = exe + ".log";
                var (codigo, _) = await Task.Run(() => Ejecutar(exe, $"/exenoui /qn /norestart /l*v {Citar(registroMsi)}", 10 * 60 * 1000));
                if (codigo == 1618)
                    throw new Exception("Windows está instalando otra cosa ahora mismo (por ejemplo, actualizaciones). Espera un poco y pulsa Reintentar.");
                if (codigo != 0 && codigo != 3010 && codigo != 1641)
                    throw new Exception($"El instalador del driver de mandos terminó con el código {codigo}. Registro: {registroMsi}");
                reiniciarPc = codigo != 0;
                if (antes == null) AnotarInstalado("vigembus");
                log("Driver de mandos instalado" + (reiniciarPc ? " (Windows pide reiniciar el PC para terminar)." : "."));
            }

            if (string.IsNullOrEmpty(OpcionSunshine("gamepad_driver")))
            {
                PonerOpcionSunshine("gamepad_driver", "vigembus");
                log("Sunshine usará el driver ViGEmBus para los mandos.");
            }
            return reiniciarPc;
        }

        /// Quita ViGEmBus (solo se llama si lo instaló RemotoFácil)
        public static bool DesinstalarViGEm()
        {
            // Su código de producto cambia con cada versión: se busca por nombre en la lista de aplicaciones
            foreach (var vista in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, vista))
                using (var raiz = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (raiz == null) continue;
                    foreach (var sub in raiz.GetSubKeyNames())
                        using (var k = raiz.OpenSubKey(sub))
                        {
                            var nombre = k?.GetValue("DisplayName") as string ?? "";
                            if (nombre.IndexOf("ViGEm", StringComparison.OrdinalIgnoreCase) < 0 &&
                                nombre.IndexOf("Virtual Gamepad Emulation", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            // El código de producto del MSI: el nombre de la clave o el que aparece en su comando
                            var guid = Regex.Match(sub + " " + (k.GetValue("UninstallString") as string ?? ""), @"\{[0-9A-Fa-f-]{36}\}");
                            if (!guid.Success) continue;
                            var (codigo, _) = Ejecutar("msiexec.exe", $"/x {guid.Value} /qn /norestart", 10 * 60 * 1000);
                            return codigo == 0 || codigo == 1605 || codigo == 3010 || codigo == 1641;
                        }
                }
            if (VersionViGEm() == null) return true;   // ni aparece en la lista ni está el driver
            // El de la 1.22.0, por si no aparece en la lista
            var (c, _) = Ejecutar("msiexec.exe", "/x {9C581C76-2D68-40F8-AA6F-94D3C5215C05} /qn /norestart", 10 * 60 * 1000);
            return c == 0 || c == 3010 || c == 1641;
        }

        public static void GuardarPantallasElegidas(IEnumerable<Monitor> apagar)
        {
            var lista = apagar?.ToList() ?? new List<Monitor>();
            if (lista.Count == 0)
            {
                if (File.Exists(Rutas.ArchivoPantallas)) File.Delete(Rutas.ArchivoPantallas);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Rutas.ArchivoPantallas));
            File.WriteAllLines(Rutas.ArchivoPantallas, lista.Select(m => m.Ruta + "\t" + m.Nombre), new UTF8Encoding(false));
        }

        public static void ReiniciarSunshine()
        {
            using (var sc = new ServiceController(ServicioSunshine))
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            // Esperar a que el panel web responda (un 401 también cuenta: está vivo)
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    var r = (HttpWebRequest)WebRequest.Create(UrlSunshineWeb + "/api/pin");
                    r.Timeout = 2000;
                    using (r.GetResponse()) return;
                }
                catch (WebException e) when (e.Response is HttpWebResponse) { return; }
                catch { }
                Thread.Sleep(1000);
            }
            throw new Exception("Sunshine no responde después de reiniciarlo.");
        }

        static HttpWebRequest PeticionSunshine(string metodo, string ruta, string usuario, string clave, int msMax)
        {
            var r = (HttpWebRequest)WebRequest.Create(UrlSunshineWeb + ruta);
            r.Method = metodo;
            r.Timeout = msMax;
            r.ReadWriteTimeout = msMax;
            r.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(usuario + ":" + clave));
            return r;
        }

        static string LeerRespuesta(HttpWebRequest r)
        {
            try
            {
                using (var resp = r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch (WebException e) when (e.Response is HttpWebResponse h)
            {
                if (h.StatusCode == HttpStatusCode.Unauthorized)
                    throw new CredencialesIncorrectas();
                string cuerpo = "";
                try { using (var sr = new StreamReader(h.GetResponseStream())) cuerpo = sr.ReadToEnd(); } catch { }
                throw new Exception($"Sunshine respondió con un error ({(int)h.StatusCode}). {cuerpo}");
            }
        }

        static Dictionary<string, object> PostJson(string ruta, string usuario, string clave, Dictionary<string, object> datos, int msMax)
        {
            var json = new JavaScriptSerializer();
            var cuerpo = Encoding.UTF8.GetBytes(json.Serialize(datos));
            var r = PeticionSunshine("POST", ruta, usuario, clave, msMax);
            r.ContentType = "application/json";
            r.ContentLength = cuerpo.Length;
            using (var s = r.GetRequestStream()) s.Write(cuerpo, 0, cuerpo.Length);
            return json.Deserialize<Dictionary<string, object>>(LeerRespuesta(r));
        }

        static bool StatusOk(Dictionary<string, object> d) => d != null && d.TryGetValue("status", out var st) && st is bool b && b;

        static IEnumerable<Dictionary<string, object>> Lista(Dictionary<string, object> d, string clave) =>
            d != null && d.TryGetValue(clave, out var v) && v is System.Collections.ArrayList l
                ? l.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();

        static string Cadena(Dictionary<string, object> d, string clave) => d.TryGetValue(clave, out var v) ? v as string : null;

        public class Solicitud { public string Id, Nombre, Direccion; }
        public class Dispositivo { public string Nombre, Uuid; }

        /// Dispositivos que han tocado este PC en Moonlight y esperan su PIN
        public static List<Solicitud> SolicitudesPendientes(string usuario, string clave)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                LeerRespuesta(PeticionSunshine("GET", "/api/pin", usuario, clave, 10000)));
            return Lista(d, "pairings").Select(p => new Solicitud
            {
                Id = Cadena(p, "id"), Nombre = Cadena(p, "name"), Direccion = Cadena(p, "address"),
            }).Where(p => !string.IsNullOrEmpty(p.Id)).ToList();
        }

        /// Busca la solicitud de emparejamiento que Moonlight acaba de hacer y le envía el PIN.
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

            return StatusOk(PostJson("/api/pin", usuario, clave, new Dictionary<string, object>
            {
                ["pairing_id"] = idSolicitud,
                ["pin"] = pin,
                ["name"] = nombre,
            }, 90000));
        }

        public static List<Dispositivo> Dispositivos(string usuario, string clave)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                LeerRespuesta(PeticionSunshine("GET", "/api/clients/list", usuario, clave, 10000)));
            return Lista(d, "named_certs")
                .Select(c => new Dispositivo { Nombre = Cadena(c, "name"), Uuid = Cadena(c, "uuid") })
                .Where(c => !string.IsNullOrEmpty(c.Uuid))
                .OrderBy(c => c.Nombre, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static bool QuitarDispositivo(string usuario, string clave, string uuid) =>
            StatusOk(PostJson("/api/clients/unpair", usuario, clave, new Dictionary<string, object> { ["uuid"] = uuid }, 15000));

        /// true si Sunshine acepta el usuario y la contraseña; false si los rechaza. Si no responde, lanza excepción.
        public static bool ComprobarCredenciales(string usuario, string clave)
        {
            try { LeerRespuesta(PeticionSunshine("GET", "/api/pin", usuario, clave, 10000)); return true; }
            catch (CredencialesIncorrectas) { return false; }
        }

        public class CredencialesIncorrectas : Exception
        {
            public CredencialesIncorrectas() : base("El usuario o la contraseña de Sunshine no son correctos.") { }
        }

        // La contraseña del panel de Sunshine se guarda cifrada con la cuenta de Windows (DPAPI) para
        // que el centro de control pueda emparejar dispositivos sin volver a pedirla.
        static readonly byte[] Entropia = Encoding.UTF8.GetBytes("RemotoFacil/sunshine");

        public static void GuardarCredenciales(string usuario, string clave)
        {
            try
            {
                Directory.CreateDirectory(Rutas.CarpetaUsuario);
                var datos = Encoding.UTF8.GetBytes(usuario + "\n" + clave);
                File.WriteAllBytes(Rutas.ArchivoCredenciales, ProtectedData.Protect(datos, Entropia, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Registro.Escribir("No se pudo guardar la contraseña de Sunshine: " + ex.Message); }
        }

        public static (string usuario, string clave)? LeerCredenciales()
        {
            try
            {
                if (!File.Exists(Rutas.ArchivoCredenciales)) return null;
                var texto = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Rutas.ArchivoCredenciales), Entropia, DataProtectionScope.CurrentUser));
                int i = texto.IndexOf('\n');
                return i > 0 ? (texto.Substring(0, i), texto.Substring(i + 1)) : ((string, string)?)null;
            }
            catch { return null; }
        }

        public static void BorrarCredenciales()
        {
            try { File.Delete(Rutas.ArchivoCredenciales); } catch { }
        }

        public static ServiceControllerStatus? EstadoServicioSunshine()
        {
            try
            {
                using (var sc = new ServiceController(ServicioSunshine))
                    return sc.Status;
            }
            catch { return null; }   // el servicio no existe
        }
    }
}
