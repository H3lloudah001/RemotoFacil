using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RemotoFacil
{
    /// <summary>
    /// "No suspender el PC mientras esté enchufado". Cada escritorio lo guarda en un sitio distinto;
    /// la primera vez se apunta el valor que había para devolverlo al desinstalar.
    /// </summary>
    static class Energia
    {
        const string EsquemaGnome = "org.gnome.settings-daemon.plugins.power";

        /// Si en este escritorio sabemos cambiarlo (si no, la casilla se oculta)
        public static bool Disponible => Distro.Escritorio switch
        {
            Escritorio.KDE => Sistema.Existe("kwriteconfig6") || Sistema.Existe("kwriteconfig5"),
            Escritorio.GNOME => Sistema.Existe("gsettings"),
            _ => false,
        };

        static string Kwrite => Sistema.Existe("kwriteconfig6") ? "kwriteconfig6" : "kwriteconfig5";
        static string Kread => Sistema.Existe("kreadconfig6") ? "kreadconfig6" : "kreadconfig5";
        static readonly string[] GrupoKde = { "--file", "powerdevilrc", "--group", "AC", "--group", "SuspendAndShutdown", "--key", "AutoSuspendAction" };

        public static void NoSuspender()
        {
            GuardarOriginal();
            switch (Distro.Escritorio)
            {
                case Escritorio.KDE:
                    Sistema.Ejecutar(Kwrite, GrupoKde.Concat(new[] { "0" }), 15000);
                    RecargarKde();
                    break;
                case Escritorio.GNOME:
                    Sistema.Ejecutar("gsettings", new[] { "set", EsquemaGnome, "sleep-inactive-ac-type", "nothing" }, 15000);
                    break;
            }
        }

        static void GuardarOriginal()
        {
            if (File.Exists(Rutas.Energia)) return;
            string valor = Distro.Escritorio switch
            {
                // Vacío = la clave no existía (Plasma usa entonces su valor por defecto)
                Escritorio.KDE => "kde=" + Sistema.Ejecutar(Kread, GrupoKde, 15000).salida.Trim(),
                Escritorio.GNOME => "gnome=" + Sistema.Ejecutar("gsettings", new[] { "get", EsquemaGnome, "sleep-inactive-ac-type" }, 15000).salida.Trim(),
                _ => null,
            };
            if (valor == null) return;
            Directory.CreateDirectory(Rutas.Config);
            File.WriteAllText(Rutas.Energia, valor);
        }

        /// Devuelve la suspensión a como estaba. false si no había nada apuntado.
        public static bool Restaurar()
        {
            if (!File.Exists(Rutas.Energia)) return false;
            var linea = File.ReadAllText(Rutas.Energia).Trim();
            int i = linea.IndexOf('=');
            if (i < 0) return false;
            string donde = linea.Substring(0, i), valor = linea.Substring(i + 1);
            if (donde == "kde")
            {
                if (valor.Length == 0) Sistema.Ejecutar(Kwrite, GrupoKde.Concat(new[] { "--delete" }), 15000);
                else Sistema.Ejecutar(Kwrite, GrupoKde.Concat(new[] { valor }), 15000);
                RecargarKde();
            }
            else if (donde == "gnome")
            {
                if (valor.Length == 0) Sistema.Ejecutar("gsettings", new[] { "reset", EsquemaGnome, "sleep-inactive-ac-type" }, 15000);
                else Sistema.Ejecutar("gsettings", new[] { "set", EsquemaGnome, "sleep-inactive-ac-type", valor }, 15000);
            }
            return true;
        }

        /// Que PowerDevil lea el cambio sin tener que cerrar la sesión
        static void RecargarKde()
        {
            foreach (var q in new[] { "qdbus6", "qdbus" })
                if (Sistema.Existe(q) && Sistema.Ejecutar(q, new[] { "org.kde.Solid.PowerManagement", "/org/kde/Solid/PowerManagement",
                                                               "org.kde.Solid.PowerManagement.refreshStatus" }, 15000).codigo == 0)
                    return;
            Sistema.Ejecutar("systemctl", new[] { "--user", "restart", "plasma-powerdevil.service" }, 30000);
        }
    }
}
