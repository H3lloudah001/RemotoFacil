using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace RemotoFacil
{
    /// Datos que se van rellenando a lo largo del asistente
    class Estado
    {
        public string Ip;
        public string Usuario;
        public string Clave;
        public bool ApagarPantallas;
    }

    static class Iconos
    {
        public static WindowIcon Ventana()
        {
            try { return new WindowIcon(AssetLoader.Open(new Uri("avares://RemotoFacil/Recursos/logo.png"))); }
            catch { return null; }
        }

        public static Avalonia.Media.Imaging.Bitmap Logo()
        {
            try { return new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri("avares://RemotoFacil/Recursos/logo.png"))); }
            catch { return null; }
        }
    }

    /// Ventana de mensaje con botones (Avalonia no trae MessageBox)
    static class Dialogo
    {
        /// Devuelve el índice del botón pulsado, o -1 si se cerró la ventana
        public static async Task<int> Mostrar(Window duena, string texto, params string[] botones)
        {
            if (botones.Length == 0) botones = new[] { "Aceptar" };
            var v = new Window
            {
                Title = "RemotoFácil", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Icon = Iconos.Ventana(),
            };
            int resultado = -1;
            var fila = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < botones.Length; i++)
            {
                int n = i;
                var b = new Button { Content = botones[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
                if (i == 0) { b.Classes.Add("accent"); b.IsDefault = true; }
                if (i == botones.Length - 1 && botones.Length > 1) b.IsCancel = true;
                b.Click += (s, e) => { resultado = n; v.Close(); };
                fila.Children.Add(b);
            }
            var cuerpo = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 20 };
            cuerpo.Children.Add(new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap });
            cuerpo.Children.Add(fila);
            v.Content = cuerpo;
            Tema.Fondo(v, "Superficie");
            await v.ShowDialog(duena);
            return resultado;
        }

        public static Task Avisar(Window duena, string texto) => Mostrar(duena, texto, "Aceptar");

        public static async Task<bool> Preguntar(Window duena, string texto, string si = "Sí", string no = "No") =>
            await Mostrar(duena, texto, si, no) == 0;
    }

    class Asistente : Window
    {
        public readonly Estado Datos;
        public readonly CancellationTokenSource AlCerrar = new CancellationTokenSource();

        readonly TextBlock titulo = new TextBlock(), subtitulo = new TextBlock();
        readonly ContentControl contenido = new ContentControl();
        readonly ScrollViewer desplazable;
        readonly Button atras = new Button(), siguiente = new Button(), cancelar = new Button();
        readonly List<Pagina> paginas;
        readonly bool suelto;
        public bool Suelto => suelto;
        int actual = -1;
        bool ocupado, terminado;

        /// Sin páginas: el asistente completo. Con páginas: solo esas, con los datos indicados (desde el centro de control).
        public Asistente(Estado datos = null, params Pagina[] soloEstas)
        {
            Datos = datos ?? new Estado();
            suelto = soloEstas.Length > 0;
            Title = "RemotoFácil";
            Icon = Iconos.Ventana();
            Width = 780; Height = 600;
            MinWidth = 640; MinHeight = 480;
            WindowStartupLocation = suelto ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            ShowInTaskbar = !suelto;
            Tema.Fondo(this, "Superficie");

            // Cabecera
            var cabecera = Tema.Fondo(new Border { Height = 92, Padding = new Thickness(28, 14, 20, 12) }, "Cabecera");
            var rejilla = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var textos = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            titulo.FontSize = 22; titulo.FontWeight = FontWeight.SemiBold; titulo.Foreground = Brushes.White;
            subtitulo.TextWrapping = TextWrapping.Wrap;
            Tema.Texto(subtitulo, "CabeceraSubtitulo");
            textos.Children.Add(titulo);
            textos.Children.Add(subtitulo);
            rejilla.Children.Add(textos);
            var botonTema = Tema.CrearBoton();
            botonTema.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(botonTema, 1);
            rejilla.Children.Add(botonTema);
            cabecera.Child = rejilla;

            // Pie
            var pie = Tema.Fondo(new Border { Padding = new Thickness(20, 12) }, "Pie");
            var botones = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in new[] { atras, siguiente, cancelar })
            {
                b.MinWidth = 120;
                b.HorizontalContentAlignment = HorizontalAlignment.Center;
                botones.Children.Add(b);
            }
            siguiente.Classes.Add("accent");
            siguiente.IsDefault = true;
            atras.Content = "Atrás";
            cancelar.Content = "Cancelar";
            atras.Click += (s, e) => Ir(actual - 1);
            siguiente.Click += async (s, e) => await Siguiente();
            cancelar.Click += (s, e) => Close();
            pie.Child = botones;

            // Margen en el contenido y no relleno en el ScrollViewer: con relleno, los textos no se ajustan al ancho visible
            contenido.Margin = new Thickness(28, 20, 28, 12);
            desplazable = new ScrollViewer
            {
                Content = contenido,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };

            var raiz = new DockPanel();
            DockPanel.SetDock(cabecera, Dock.Top);
            DockPanel.SetDock(pie, Dock.Bottom);
            raiz.Children.Add(cabecera);
            raiz.Children.Add(pie);
            raiz.Children.Add(desplazable);
            Content = raiz;

            paginas = suelto
                ? soloEstas.ToList()
                : new List<Pagina>
                {
                    new PaginaBienvenida(), new PaginaInstalar(), new PaginaTailscale(), new PaginaSunshine(),
                    new PaginaPantallas(), new PaginaEmparejar(), new PaginaFin(),
                };
            foreach (var p in paginas) p.A = this;
            Opened += (s, e) => Ir(0);
        }

        void Ir(int i)
        {
            if (i < 0 || i >= paginas.Count) return;
            actual = i;
            var p = paginas[i];
            titulo.Text = p.Titulo;
            subtitulo.Text = p.Subtitulo;
            contenido.Content = p;
            desplazable.Offset = default;
            p.AlMostrar();
            Refrescar();
        }

        /// Saltar una página que no aplica (p. ej. pantallas en un escritorio sin soporte)
        public void Saltar() => Dispatcher(() => Ir(actual + 1));

        static void Dispatcher(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

        bool EsUltima => actual == paginas.Count - 1;

        async Task Siguiente()
        {
            if (ocupado || !paginas[actual].PuedeSeguir) return;
            bool seguir;
            ocupado = true; Refrescar();
            try { seguir = await paginas[actual].AlSiguiente(); }
            catch (Exception ex) { await Error(ex.Message); seguir = false; }
            finally { ocupado = false; }
            if (!seguir) { Refrescar(); return; }
            if (EsUltima) { terminado = true; Close(); }
            else Ir(actual + 1);
        }

        public void Refrescar()
        {
            if (actual < 0) return;
            var p = paginas[actual];
            bool soloCerrar = suelto && p.TextoSiguiente == Pagina.TextoPorDefecto;
            atras.IsVisible = !suelto;
            atras.IsEnabled = !ocupado && !p.Ocupada && actual > 0 && !EsUltima;
            siguiente.IsEnabled = !ocupado && !p.Ocupada && p.PuedeSeguir;
            siguiente.Content = soloCerrar ? "Cerrar" : suelto ? p.TextoSiguiente : EsUltima ? "Terminar" : p.TextoSiguiente;
            cancelar.IsVisible = suelto ? !soloCerrar : !EsUltima;
        }

        public Task Error(string mensaje) => Dialogo.Avisar(this, mensaje);

        /// Abre la desinstalación encima; si se llega a desinstalar, esta ventana se cierra también
        public async Task Desinstalar()
        {
            var pagina = new PaginaDesinstalar();
            await new Asistente(new Estado(), pagina).ShowDialog(this);
            if (pagina.Hecho) { terminado = true; Close(); }
        }

        bool confirmado;

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Una tarea a medias (instalando, desinstalando…) no se interrumpe
            if (actual >= 0 && (paginas[actual].Ocupada || ocupado) && (suelto || paginas[actual] is PaginaInstalar))
            {
                e.Cancel = true;
                return;
            }
            if (!suelto && !terminado && !confirmado && actual > 0 && !EsUltima)
            {
                e.Cancel = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    if (await Dialogo.Preguntar(this, "¿Seguro que quieres salir? La configuración quedará a medias.\n" +
                                                      "Puedes volver a abrir RemotoFácil más tarde para terminarla."))
                    {
                        confirmado = true;
                        Close();
                    }
                });
                return;
            }
            AlCerrar.Cancel();
            base.OnClosing(e);
        }
    }

    // ======================================================================
    // Base de las páginas: una columna; cada elemento va debajo del anterior
    // ======================================================================

    abstract class Pagina : StackPanel
    {
        public const string TextoPorDefecto = "Siguiente";

        public Asistente A;
        public abstract string Titulo { get; }
        public abstract string Subtitulo { get; }
        public virtual string TextoSiguiente => TextoPorDefecto;
        public virtual bool PuedeSeguir => true;
        public bool Ocupada { get; protected set; }
        public virtual void AlMostrar() { }
        public virtual Task<bool> AlSiguiente() => Task.FromResult(true);

        protected T Poner<T>(T c, double abajo = 10) where T : Control
        {
            c.Margin = new Thickness(c.Margin.Left, 0, 0, abajo);
            Children.Add(c);
            return c;
        }

        protected TextBlock Texto(string texto, bool negrita = false, double abajo = 10)
        {
            var t = new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap, IsVisible = texto.Length > 0 };
            if (negrita) { t.FontWeight = FontWeight.SemiBold; t.FontSize = 15; }
            // Los textos de estado vacíos no dejan hueco: aparecen cuando reciben algo
            t.PropertyChanged += (s, e) => { if (e.Property == TextBlock.TextProperty) t.IsVisible = !string.IsNullOrEmpty(t.Text); };
            return Poner(t, abajo);
        }

        protected TextBlock Suave(string texto, double abajo = 10) => Tema.Texto(Texto(texto, abajo: abajo), "Suave");

        protected Button Boton(string texto, bool principal = false)
        {
            var b = new Button { Content = texto };
            if (principal) b.Classes.Add("accent");
            return Poner(b);
        }

        protected StackPanel Fila(double abajo = 10) =>
            Poner(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }, abajo);

        /// Pone un texto de estado con su color ("Exito", "Error", "Suave", "Texto"…)
        protected static void Estado(TextBlock t, string texto, string color)
        {
            t.Text = texto;
            Tema.Texto(t, color);
            t.IsVisible = texto.Length > 0;
        }

        protected void Avisar() => A?.Refrescar();
    }
}
