using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemotoFacil
{
    /// <summary>
    /// Instala Tailscale y Sunshine con lo que use cada distro. Primero se descarga lo necesario como
    /// usuario normal y luego todo lo que necesita permisos va en un único script de administrador
    /// (una sola petición de contraseña).
    /// </summary>
    static class Instalador
    {
        static readonly HttpClient Http = CrearHttp();

        static HttpClient CrearHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("RemotoFacil/1.0");
            return h;
        }

        static Distro D => Distro.Actual;

        /// Cómo se va a instalar cada cosa en esta distro (para enseñarlo y para el registro)
        public static string MetodoTailscale => D.Familia switch
        {
            Familia.Debian => "repositorio oficial de Tailscale (apt)",
            Familia.Fedora => "repositorio oficial de Tailscale (dnf)",
            Familia.Suse => "repositorio oficial de Tailscale (zypper)",
            Familia.Arch => "repositorios de Arch (pacman)",
            _ => "binarios oficiales de Tailscale",
        };

        public static string MetodoSunshine => D.Familia switch
        {
            Familia.Debian => "paquete .deb oficial",
            Familia.Fedora or Familia.Suse => "paquete .rpm oficial",
            Familia.Arch => "pacman",
            _ => "Flatpak (Flathub)",
        };

        public class Plan
        {
            public bool Tailscale, Sunshine;
            public bool SunshineFlatpak;
            public readonly StringBuilder Script = new StringBuilder();
        }

        /// Descarga lo necesario y prepara el script de administrador
        public static async Task<Plan> Preparar(IProgress<string> log, CancellationToken cancelar)
        {
            var plan = new Plan { Tailscale = !Tailscale.Instalado, Sunshine = !Sunshine.Instalado };
            var dir = Path.Combine(Rutas.Cache, "descargas");
            Directory.CreateDirectory(dir);
            var s = plan.Script;
            s.AppendLine("#!/bin/bash");
            s.AppendLine("# Generado por RemotoFácil");
            s.AppendLine("set -u");
            s.AppendLine("export LC_ALL=C DEBIAN_FRONTEND=noninteractive");
            s.AppendLine($"USUARIO={Sistema.Q(Sistema.Usuario)}");
            s.AppendLine("paso() { echo \"### $*\"; }");
            s.AppendLine("falla() { echo \"ERROR: $*\"; exit 1; }");

            // ---------- Tailscale ----------
            if (plan.Tailscale)
            {
                s.AppendLine("paso 'Instalando Tailscale'");
                switch (D.Familia)
                {
                    case Familia.Debian:
                    {
                        var (base_, codigo) = D.CodigoUbuntu.Length > 0 || D.Id == "ubuntu"
                            ? ("ubuntu", D.CodigoUbuntu.Length > 0 ? D.CodigoUbuntu : D.Codigo)
                            : ("debian", D.CodigoDebian.Length > 0 ? D.CodigoDebian : D.Codigo);
                        var url = $"https://pkgs.tailscale.com/stable/{base_}/{codigo}";
                        var llave = await Descargar(url + ".noarmor.gpg", Path.Combine(dir, "tailscale.gpg"), log, cancelar);
                        var lista = await Descargar(url + ".tailscale-keyring.list", Path.Combine(dir, "tailscale.list"), log, cancelar);
                        s.AppendLine($"install -m 644 {Sistema.Q(llave)} /usr/share/keyrings/tailscale-archive-keyring.gpg || falla 'no se pudo copiar la llave de Tailscale'");
                        s.AppendLine($"install -m 644 {Sistema.Q(lista)} /etc/apt/sources.list.d/tailscale.list || falla 'no se pudo añadir el repositorio de Tailscale'");
                        // Un repositorio ajeno caído no debe parar todo: si falta algo, fallará la instalación
                        s.AppendLine("apt-get update || echo 'Aviso: algún repositorio no respondió'");
                        s.AppendLine("apt-get install -y tailscale || falla 'no se pudo instalar Tailscale'");
                        break;
                    }
                    case Familia.Fedora:
                    {
                        var repo = await Descargar("https://pkgs.tailscale.com/stable/fedora/tailscale.repo", Path.Combine(dir, "tailscale.repo"), log, cancelar);
                        s.AppendLine($"install -m 644 {Sistema.Q(repo)} /etc/yum.repos.d/tailscale.repo || falla 'no se pudo añadir el repositorio de Tailscale'");
                        s.AppendLine("dnf install -y tailscale || falla 'no se pudo instalar Tailscale'");
                        break;
                    }
                    case Familia.Suse:
                    {
                        var rama = D.Id.Contains("tumbleweed") || D.Id.Contains("slowroll") ? "tumbleweed" : $"leap/{D.Version}";
                        s.AppendLine($"zypper --non-interactive addrepo -f -g https://pkgs.tailscale.com/stable/opensuse/{rama}/tailscale.repo 2>/dev/null || true");
                        s.AppendLine("zypper --non-interactive --gpg-auto-import-keys refresh || echo 'Aviso: algún repositorio no respondió'");
                        s.AppendLine("zypper --non-interactive install tailscale || falla 'no se pudo instalar Tailscale'");
                        break;
                    }
                    case Familia.Arch:
                        // Si la base de datos local está vieja, el paquete puede no encontrarse: se sincroniza y se reintenta
                        s.AppendLine("pacman -S --needed --noconfirm tailscale || pacman -Sy --needed --noconfirm tailscale || falla 'no se pudo instalar Tailscale'");
                        break;
                    default:
                        await PrepararTailscaleSuelto(s, dir, log, cancelar);
                        break;
                }
            }
            s.AppendLine("paso 'Activando el servicio de Tailscale'");
            s.AppendLine("systemctl enable --now tailscaled || falla 'no se pudo arrancar tailscaled'");
            s.AppendLine("for i in $(seq 1 30); do [ -S /run/tailscale/tailscaled.sock ] && break; sleep 1; done");
            // Operador: así RemotoFácil puede iniciar sesión y ver el estado sin pedir la contraseña cada vez
            s.AppendLine($"TS=$(command -v tailscale || echo {Tailscale.CarpetaSuelta}/tailscale)");
            s.AppendLine("\"$TS\" set --operator=\"$USUARIO\" || \"$TS\" up --operator=\"$USUARIO\" --timeout=1s >/dev/null 2>&1 || true");

            // ---------- Sunshine ----------
            if (plan.Sunshine)
            {
                s.AppendLine("paso 'Instalando Sunshine'");
                // En CachyOS (y con chaotic-aur) Sunshine está en los repositorios; en Arch puro solo en AUR
                bool enRepositorio = D.Familia == Familia.Arch && Sistema.Ejecutar("pacman", new[] { "-Si", "sunshine" }, 30000).codigo == 0;
                // Si no, el paquete oficial de la última versión; y si no hay para esta distro, Flatpak
                string archivo = !enRepositorio && D.Familia is Familia.Arch or Familia.Debian or Familia.Fedora or Familia.Suse
                    ? await DescargarPaqueteSunshine(dir, log, cancelar) : null;

                if (enRepositorio)
                    s.AppendLine("pacman -S --needed --noconfirm sunshine || falla 'no se pudo instalar Sunshine'");
                else if (archivo != null)
                {
                    var q = Sistema.Q(archivo);
                    s.AppendLine(D.Familia switch
                    {
                        Familia.Arch => $"pacman -U --needed --noconfirm {q} || falla 'no se pudo instalar Sunshine'",
                        Familia.Debian => $"apt-get update; apt-get install -y {q} || falla 'no se pudo instalar Sunshine'",
                        Familia.Fedora => $"dnf install -y {q} || falla 'no se pudo instalar Sunshine'",
                        _ => $"zypper --non-interactive --no-gpg-checks install --allow-unsigned-rpm {q} || falla 'no se pudo instalar Sunshine'",
                    });
                }
                else
                {
                    if (!Sistema.Existe("flatpak"))
                        throw new Exception("No hay un paquete de Sunshine para esta distribución y tampoco está Flatpak instalado.\n" +
                                            "Instala Flatpak (o Sunshine) y vuelve a intentarlo.");
                    plan.SunshineFlatpak = true;
                    s.AppendLine("flatpak remote-add --if-not-exists --system flathub https://dl.flathub.org/repo/flathub.flatpakrepo || true");
                    s.AppendLine($"flatpak install -y --noninteractive --system flathub {Sunshine.IdFlatpak} || falla 'no se pudo instalar Sunshine (Flatpak)'");
                }
            }

            if (!plan.SunshineFlatpak && (Sunshine.Nativo != null || plan.Sunshine))
            {
                // Captura de pantalla por KMS (la más fiable en Wayland) y control de teclado/ratón (uinput).
                // Los paquetes oficiales suelen hacerlo ya; repetirlo no hace daño.
                s.AppendLine("paso 'Dando permisos a Sunshine'");
                s.AppendLine("SB=$(command -v sunshine) && command -v setcap >/dev/null && setcap cap_sys_admin+p \"$(readlink -f \"$SB\")\" || true");
            }
            s.AppendLine("if ! grep -qs uinput /usr/lib/udev/rules.d/*sunshine* /etc/udev/rules.d/*sunshine* /usr/lib/udev/rules.d/*uinput* /etc/udev/rules.d/*uinput*; then");
            s.AppendLine("  echo 'KERNEL==\"uinput\", SUBSYSTEM==\"misc\", OPTIONS+=\"static_node=uinput\", TAG+=\"uaccess\"' > /etc/udev/rules.d/60-remotofacil-uinput.rules");
            s.AppendLine("  udevadm control --reload-rules; udevadm trigger --property-match=DEVNAME=/dev/uinput || true");
            s.AppendLine("fi");
            s.AppendLine("modprobe uinput 2>/dev/null || true");
            s.AppendLine("echo uinput > /etc/modules-load.d/remotofacil-uinput.conf 2>/dev/null || true");

            // ---------- Cortafuegos: solo se abre la interfaz de Tailscale ----------
            s.AppendLine("paso 'Configurando el cortafuegos'");
            s.AppendLine("if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -qiE '^(status|estado): *(active|activo)'; then");
            s.AppendLine("  ufw allow in on tailscale0 comment RemotoFacil || true");
            s.AppendLine("fi");
            s.AppendLine("if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then");
            s.AppendLine("  firewall-cmd --permanent --zone=trusted --add-interface=tailscale0 && firewall-cmd --reload || true");
            s.AppendLine("fi");
            s.AppendLine("paso 'Listo'");
            return plan;
        }

        /// Tailscale sin paquete: los binarios oficiales en /var/lib (que se puede escribir incluso en SteamOS) y su servicio
        static async Task PrepararTailscaleSuelto(StringBuilder s, string dir, IProgress<string> log, CancellationToken cancelar)
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://pkgs.tailscale.com/stable/?mode=json", cancelar));
            var nombre = doc.RootElement.GetProperty("Tarballs").GetProperty("amd64").GetString();
            var tgz = await Descargar("https://pkgs.tailscale.com/stable/" + nombre, Path.Combine(dir, "tailscale.tgz"), log, cancelar);
            var c = Tailscale.CarpetaSuelta;
            s.AppendLine($"mkdir -p {c} && tar -xzf {Sistema.Q(tgz)} --strip-components=1 -C {c} || falla 'no se pudo descomprimir Tailscale'");
            s.AppendLine("if [ ! -e /usr/lib/systemd/system/tailscaled.service ]; then");
            s.AppendLine("cat > /etc/systemd/system/tailscaled.service <<'FIN'");
            s.AppendLine("[Unit]");
            s.AppendLine("Description=Tailscale (instalado por RemotoFacil)");
            s.AppendLine("Wants=network-pre.target");
            s.AppendLine("After=network-pre.target NetworkManager.service systemd-resolved.service");
            s.AppendLine("[Service]");
            s.AppendLine($"ExecStart={c}/tailscaled --state=/var/lib/tailscale/tailscaled.state --socket=/run/tailscale/tailscaled.sock --port=41641");
            s.AppendLine($"ExecStopPost={c}/tailscaled --cleanup");
            s.AppendLine("Restart=on-failure");
            s.AppendLine("RuntimeDirectory=tailscale");
            s.AppendLine("RuntimeDirectoryMode=0755");
            s.AppendLine("StateDirectory=tailscale");
            s.AppendLine("StateDirectoryMode=0700");
            s.AppendLine("Type=notify");
            s.AppendLine("[Install]");
            s.AppendLine("WantedBy=multi-user.target");
            s.AppendLine("FIN");
            s.AppendLine("systemctl daemon-reload");
            s.AppendLine("fi");
        }

        /// Elige el paquete de la última versión de Sunshine que mejor encaja con esta distro
        static async Task<string> DescargarPaqueteSunshine(string dir, IProgress<string> log, CancellationToken cancelar)
        {
            log.Report("Buscando la última versión de Sunshine…");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/repos/LizardByte/Sunshine/releases/latest", cancelar));
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => (nombre: a.GetProperty("name").GetString(), url: a.GetProperty("browser_download_url").GetString())).ToList();
            var elegido = ElegirPaquete(assets.Select(a => a.nombre).ToList());
            if (elegido == null) return null;
            log.Report("Paquete elegido: " + elegido);
            return await Descargar(assets.First(a => a.nombre == elegido).url, Path.Combine(dir, elegido), log, cancelar);
        }

        static readonly Dictionary<string, string> VersionesUbuntu = new()
        {
            ["jammy"] = "22.04", ["noble"] = "24.04", ["oracular"] = "24.10", ["plucky"] = "25.04",
            ["questing"] = "25.10", ["resolute"] = "26.04",
        };

        /// El paquete hecho exactamente para esta versión de la distro. Si no lo hay, null (y se usa Flatpak):
        /// un paquete de otra versión suele pedir bibliotecas que no existen aquí.
        public static string ElegirPaquete(List<string> nombres)
        {
            string Termina(string fin) => nombres.FirstOrDefault(n => n.EndsWith(fin));
            switch (D.Familia)
            {
                case Familia.Arch:
                    return nombres.FirstOrDefault(n => Regex.IsMatch(n, @"-x86_64\.pkg\.tar\.zst$"));
                case Familia.Fedora:
                    return Termina($".fc{D.Version}.x86_64.rpm");
                case Familia.Suse:
                    // Tumbleweed (y Slowroll) o Leap 16
                    if (D.Id.Contains("tumbleweed") || D.Id.Contains("slowroll")) return Termina(".suse.tw.x86_64.rpm");
                    return Termina($".suse.lp{D.Version.Replace(".", "")}.x86_64.rpm");
                case Familia.Debian:
                    if (D.CodigoUbuntu.Length > 0 || D.Id == "ubuntu")
                    {
                        // Mint, Pop!_OS, Zorin…: su base de Ubuntu
                        var v = D.Id == "ubuntu" ? D.Version : VersionesUbuntu.GetValueOrDefault(D.CodigoUbuntu, "");
                        return v.Length > 0 ? Termina($"+ubuntu{v}_amd64.deb") : null;
                    }
                    return Termina($"+debian{(D.CodigoDebian.Length > 0 ? D.CodigoDebian : D.Codigo)}_amd64.deb");
                default:
                    return null;
            }
        }

        static async Task<string> Descargar(string url, string destino, IProgress<string> log, CancellationToken cancelar)
        {
            log.Report("Descargando " + Path.GetFileName(new Uri(url).AbsolutePath) + "…");
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancelar);
            if (!resp.IsSuccessStatusCode) throw new Exception($"No se pudo descargar {url} ({(int)resp.StatusCode}).");
            await using (var f = File.Create(destino))
                await resp.Content.CopyToAsync(f, cancelar);
            // Que root pueda leerlo aunque la carpeta personal sea restrictiva
            File.SetUnixFileMode(destino, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            return destino;
        }

        // ---------- Pasos como usuario, después del script de administrador ----------

        public static void DespuesDelAdministrador(Plan plan, IProgress<string> log)
        {
            if (plan.SunshineFlatpak || Sunshine.EsFlatpak)
            {
                // El propio Flatpak trae un script que crea el servicio de usuario y las reglas de uinput
                log.Report("Configurando Sunshine (Flatpak)…");
                var (cod, salida) = Sistema.Ejecutar("flatpak", new[] { "run", "--command=additional-install.sh", Sunshine.IdFlatpak }, 5 * 60 * 1000);
                if (cod != 0) log.Report("Aviso: " + salida);
            }
            log.Report("Activando Sunshine…");
            Sunshine.ActivarServicio();
            if (plan.Tailscale) AnotarInstalado("tailscale");
            if (plan.Sunshine) AnotarInstalado(plan.SunshineFlatpak ? "sunshine-flatpak" : "sunshine");
        }

        public static void AnotarInstalado(string programa)
        {
            try
            {
                Directory.CreateDirectory(Rutas.Config);
                var ya = File.Exists(Rutas.Instalados) ? File.ReadAllLines(Rutas.Instalados) : Array.Empty<string>();
                if (!ya.Contains(programa)) File.AppendAllLines(Rutas.Instalados, new[] { programa });
            }
            catch (Exception ex) { Registro.Escribir("No se pudo anotar la instalación: " + ex.Message); }
        }

        /// true si lo instaló RemotoFácil; null si no se sabe
        public static bool? LoInstaloRemotoFacil(string programa)
        {
            try
            {
                if (!File.Exists(Rutas.Instalados)) return null;
                var l = File.ReadAllLines(Rutas.Instalados);
                return l.Contains(programa) || (programa == "sunshine" && l.Contains("sunshine-flatpak"));
            }
            catch { return null; }
        }
    }
}
