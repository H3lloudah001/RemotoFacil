using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace RemotoFacil
{
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
            return Construir().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder Construir() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
    }

    class App : Application
    {
        public override void Initialize()
        {
            Name = "RemotoFácil";
            Styles.Add(new FluentTheme());
            Tema.Inicializar(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime vida)
            {
                var args = vida.Args ?? Array.Empty<string>();
                vida.MainWindow = args.Contains("--desinstalar") ? new Asistente(new Estado(), new PaginaDesinstalar())
                                : !args.Contains("--asistente") && Integracion.YaConfigurado ? new CentroControl()
                                : new Asistente();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
