using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemotoFacil
{
    /// <summary>
    /// Quita lo que instaló y configuró RemotoFácil. Tailscale y Sunshine son opcionales: si ya estaban
    /// antes de RemotoFácil no se marcan, y si se dejan solo se les quita lo que añadió RemotoFácil.
    /// </summary>
    class PaginaDesinstalar : Pagina
    {
        public override string Titulo => "Desinstalar RemotoFácil";
        public override string Subtitulo => "Quita los programas y los ajustes que dejó en este PC";
        public override string TextoSiguiente => Hecho ? TextoPorDefecto : "Desinstalar";
        public override bool PuedeSeguir => !Ocupada;

        public bool Hecho { get; private set; }

        readonly CheckBox tailscale = new CheckBox { Content = "Tailscale (antes cierra la sesión de este PC en tu cuenta)" };
        readonly CheckBox sunshine = new CheckBox { Content = "Sunshine, con su contraseña y los dispositivos emparejados" };
        readonly CheckBox propio = new CheckBox { Content = "RemotoFácil y lo que configuró (accesos, pantallas, energía, cortafuegos…)", IsChecked = true };
        readonly TextBlock notaTailscale, notaSunshine, estado;
        readonly TextBox registro = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140,
            FontFamily = new FontFamily("monospace"), FontSize = 12, IsVisible = false,
        };

        public PaginaDesinstalar()
        {
            Texto("Elige qué quieres quitar de este PC:", abajo: 8);
            Poner(tailscale, 0);
            notaTailscale = Nota();
            Poner(sunshine, 0);
            notaSunshine = Nota();
            Poner(propio, 10);
            // Siempre marcada
            propio.IsCheckedChanged += (s, e) => propio.IsChecked = true;
            Suave("Lo que no marques seguirá funcionando por su cuenta: RemotoFácil solo le quita lo que añadió él.");
            estado = Texto("", abajo: 6);
            estado.IsVisible = false;
            Poner(registro, 0);
        }

        TextBlock Nota()
        {
            var t = Suave("", abajo: 6);
            t.Margin = new Thickness(28, 0, 0, 6);
            return t;
        }

        public override void AlMostrar()
        {
            Preparar(tailscale, notaTailscale, Tailscale.Instalado, "tailscale", "Tailscale");
            Preparar(sunshine, notaSunshine, Sunshine.Instalado, "sunshine", "Sunshine");
        }

        static void Preparar(CheckBox casilla, TextBlock nota, bool instalado, string clave, string nombre)
        {
            casilla.IsVisible = instalado;
            var deRemotoFacil = Instalador.LoInstaloRemotoFacil(clave);
            casilla.IsChecked = instalado && deRemotoFacil != false;
            nota.Text = instalado && deRemotoFacil == false ? $"{nombre} ya estaba instalado antes de RemotoFácil, así que por defecto se deja." : "";
            nota.IsVisible = nota.Text.Length > 0;
        }

        void Log(string t) => Ui.EnUi(() =>
        {
            registro.Text += (t.StartsWith("### ") ? t.Substring(4) : t) + "\n";
            registro.CaretIndex = registro.Text.Length;
        });

        public override async Task<bool> AlSiguiente()
        {
            if (Hecho) return true;
            bool quitarTs = tailscale.IsVisible && tailscale.IsChecked == true;
            bool quitarSs = sunshine.IsVisible && sunshine.IsChecked == true;
            var quitar = new List<string>();
            if (quitarTs) quitar.Add("Tailscale");
            if (quitarSs) quitar.Add("Sunshine");
            quitar.Add("RemotoFácil");
            if (!await Dialogo.Preguntar(A, "Se va a quitar: " + string.Join(", ", quitar) + ".\n\n" +
                                            "Después ya no podrás conectarte a este PC desde otros dispositivos. ¿Seguir?"))
                return false;

            tailscale.IsEnabled = sunshine.IsEnabled = false;
            registro.IsVisible = true;
            Ocupada = true; Avisar();
            Estado(estado, "Desinstalando… puede tardar un par de minutos.", "Texto");
            List<string> pendientes;
            try { pendientes = await Desinstalador.Ejecutar(quitarTs, quitarSs, Log); }
            catch (Exception ex) { pendientes = new List<string> { "Error inesperado: " + ex.Message }; }
            Ocupada = false;
            Hecho = true;
            if (pendientes.Count == 0) Estado(estado, "Listo: RemotoFácil se ha desinstalado.", "Exito");
            else Estado(estado, "Desinstalado, pero hay cosas que revisar:\n•  " + string.Join("\n•  ", pendientes), "Aviso");
            Avisar();
            return false;
        }
    }

    static class Desinstalador
    {
        static Distro D => Distro.Actual;

        public static async Task<List<string>> Ejecutar(bool quitarTailscale, bool quitarSunshine, Action<string> log)
        {
            var pendientes = new List<string>();
            async Task Paso(string que, Func<Task> accion)
            {
                try { await accion(); }
                catch (Exception ex)
                {
                    log($"No se pudo {que}: {ex.Message}");
                    pendientes.Add($"No se pudo {que}.");
                }
            }
            Task Hacer(Action a) => Task.Run(a);

            // 1. Pantallas que hubieran quedado apagadas
            if (File.Exists(Rutas.EstadoPantallas))
                await Paso("volver a encender las pantallas", () => Hacer(() => { Pantallas.Restaurar(forzar: false); log("Pantallas restauradas."); }));

            // 2. Lo que se hace como usuario antes de quitar paquetes
            bool sunshineFlatpak = Sunshine.EsFlatpak;
            if (quitarSunshine)
                await Paso("parar Sunshine", () => Hacer(() =>
                {
                    if (Sunshine.Servicio() is string s) Sistema.Ejecutar("systemctl", new[] { "--user", "disable", "--now", s }, 60000);
                    if (File.Exists(Sunshine.ServicioPropio) && File.ReadAllText(Sunshine.ServicioPropio).Contains("RemotoFacil"))
                    {
                        File.Delete(Sunshine.ServicioPropio);
                        Sistema.Ejecutar("systemctl", new[] { "--user", "daemon-reload" }, 30000);
                    }
                    if (sunshineFlatpak)
                        Sistema.Ejecutar("flatpak", new[] { "run", "--command=remove-additional-install.sh", Sunshine.IdFlatpak }, 120000);
                    log("Sunshine detenido.");
                }));
            else if (Sunshine.Instalado)
                await Paso("quitar los comandos de RemotoFácil de Sunshine", () => Hacer(() =>
                {
                    Sunshine.ConfigurarPantallas(false);
                    try { Sunshine.Reiniciar(); } catch { }
                    log("Sunshine ya no apagará pantallas al conectarse.");
                }));
            if (quitarTailscale && Tailscale.Instalado)
                await Paso("cerrar la sesión de Tailscale", () => Hacer(() => { Tailscale.CerrarSesion(); log("Sesión de Tailscale cerrada."); }));

            // 3. Todo lo que necesita administrador, en un solo script
            var script = ScriptAdministrador(quitarTailscale, quitarSunshine, sunshineFlatpak);
            log("Pidiendo la contraseña de administrador…");
            int cod = await Sistema.ComoAdministrador(script, log);
            if (cod == 126 || cod == 127) pendientes.Add("No se dio la contraseña de administrador: los programas siguen instalados.");
            else if (cod != 0) pendientes.Add($"Algunos pasos de administrador fallaron (código {cod}); revisa el registro de arriba.");

            // 4. Lo propio de RemotoFácil, en la carpeta del usuario
            if (quitarSunshine && cod == 0)
                await Paso("borrar la configuración de Sunshine", () => Hacer(() =>
                {
                    var dir = Sunshine.CarpetaConfig;
                    if (Directory.Exists(dir)) { Directory.Delete(dir, true); log("Borrado: " + dir); }
                }));
            await Paso("devolver la configuración de energía", () => Hacer(() =>
            {
                if (Energia.Restaurar()) log("Suspensión del PC devuelta a como estaba.");
            }));
            await Paso("quitar los accesos directos", () => Hacer(() => { Integracion.Desinstalar(); log("Accesos directos quitados."); }));
            Registro.Desactivado = true;
            await Paso("borrar los ajustes de RemotoFácil", () => Hacer(() =>
            {
                // En Linux se puede borrar un ejecutable aunque se esté usando: el AppImage se borra también
                foreach (var dir in new[] { Rutas.Config, Rutas.Estado, Rutas.Cache, Rutas.Datos })
                    if (Directory.Exists(dir)) { Directory.Delete(dir, true); log("Borrado: " + dir); }
            }));
            log(pendientes.Count == 0 ? "Terminado." : "Terminado, con avisos.");
            return pendientes;
        }

        /// Quita paquetes, servicios, reglas y archivos de sistema según cómo se instalaron en esta distro
        static string ScriptAdministrador(bool quitarTailscale, bool quitarSunshine, bool sunshineFlatpak)
        {
            var s = new StringBuilder();
            s.AppendLine("#!/bin/bash");
            s.AppendLine("set -u");
            s.AppendLine("export LC_ALL=C DEBIAN_FRONTEND=noninteractive");
            s.AppendLine("paso() { echo \"### $*\"; }");
            s.AppendLine("ERRORES=0");

            if (quitarSunshine)
            {
                s.AppendLine("paso 'Desinstalando Sunshine'");
                if (sunshineFlatpak)
                    s.AppendLine($"flatpak uninstall -y --noninteractive {Sunshine.IdFlatpak} || ERRORES=1");
                else
                    s.AppendLine(D.Familia switch
                    {
                        Familia.Arch => "pacman -Rns --noconfirm sunshine || ERRORES=1",
                        Familia.Debian => "apt-get remove -y --purge sunshine || ERRORES=1",
                        // El paquete .rpm oficial se llama "Sunshine"; el de algunos repositorios, "sunshine"
                        Familia.Fedora => "for p in Sunshine sunshine; do rpm -q $p >/dev/null 2>&1 && { dnf remove -y $p || ERRORES=1; }; done",
                        Familia.Suse => "for p in Sunshine sunshine; do rpm -q $p >/dev/null 2>&1 && { zypper --non-interactive remove $p || ERRORES=1; }; done",
                        _ => "echo 'Sunshine no se instaló con un paquete que RemotoFácil sepa quitar'; ERRORES=1",
                    });
            }
            if (quitarTailscale)
            {
                s.AppendLine("paso 'Desinstalando Tailscale'");
                s.AppendLine("systemctl disable --now tailscaled 2>/dev/null || true");
                if (Tailscale.Cli?.StartsWith(Tailscale.CarpetaSuelta) == true)
                {
                    s.AppendLine($"rm -rf {Tailscale.CarpetaSuelta}");
                    s.AppendLine("grep -qs RemotoFacil /etc/systemd/system/tailscaled.service && rm -f /etc/systemd/system/tailscaled.service");
                    s.AppendLine("systemctl daemon-reload");
                }
                else
                    s.AppendLine(D.Familia switch
                    {
                        Familia.Arch => "pacman -Rns --noconfirm tailscale || ERRORES=1",
                        Familia.Debian => "apt-get remove -y --purge tailscale || ERRORES=1; rm -f /etc/apt/sources.list.d/tailscale.list /usr/share/keyrings/tailscale-archive-keyring.gpg",
                        Familia.Fedora => "dnf remove -y tailscale || ERRORES=1; rm -f /etc/yum.repos.d/tailscale.repo",
                        Familia.Suse => "zypper --non-interactive remove tailscale || ERRORES=1; zypper --non-interactive removerepo tailscale-stable 2>/dev/null || true",
                        _ => "echo 'Tailscale vino con el sistema: se deja instalado, solo se desactiva'",
                    });
                // Su estado: la identidad de este PC en la red
                s.AppendLine("rm -rf /var/lib/tailscale");
                s.AppendLine("rmdir /var/lib/remotofacil 2>/dev/null || true");
            }

            s.AppendLine("paso 'Quitando reglas de RemotoFácil'");
            s.AppendLine("rm -f /etc/udev/rules.d/60-remotofacil-uinput.rules /etc/modules-load.d/remotofacil-uinput.conf");
            s.AppendLine("udevadm control --reload-rules 2>/dev/null || true");
            s.AppendLine("if command -v ufw >/dev/null; then ufw delete allow in on tailscale0 >/dev/null 2>&1 || true; fi");
            s.AppendLine("if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then");
            s.AppendLine("  firewall-cmd --permanent --zone=trusted --remove-interface=tailscale0 >/dev/null 2>&1 && firewall-cmd --reload >/dev/null 2>&1 || true");
            s.AppendLine("fi");
            s.AppendLine("paso 'Listo'");
            s.AppendLine("exit $ERRORES");
            return s.ToString();
        }
    }
}
