using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace RemotoFacil
{
    enum ModoTema { Automatico, Claro, Oscuro }

    /// <summary>
    /// Claro u oscuro. Por defecto sigue al sistema (Avalonia lo lee del portal de escritorio, que
    /// funciona en KDE y GNOME). Los colores propios están en dos diccionarios y los controles se enlazan
    /// a ellos por nombre, así que al cambiar de tema se actualizan solos.
    /// </summary>
    static class Tema
    {
        public static event Action Cambio;

        static ModoTema preferencia = Leer();

        static ModoTema Leer()
        {
            try { return File.Exists(Rutas.Tema) && Enum.TryParse(File.ReadAllText(Rutas.Tema).Trim(), out ModoTema m) ? m : ModoTema.Automatico; }
            catch { return ModoTema.Automatico; }
        }

        public static ModoTema Preferencia
        {
            get => preferencia;
            set
            {
                preferencia = value;
                try { Directory.CreateDirectory(Rutas.Config); File.WriteAllText(Rutas.Tema, value.ToString()); } catch { }
                if (Application.Current != null) Application.Current.RequestedThemeVariant = Variante;
                Cambio?.Invoke();
            }
        }

        static ThemeVariant Variante => preferencia switch
        {
            ModoTema.Claro => ThemeVariant.Light,
            ModoTema.Oscuro => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        public static string NombrePreferencia => preferencia switch
        {
            ModoTema.Claro => "Claro",
            ModoTema.Oscuro => "Oscuro",
            _ => "Automático",
        };

        public static void Inicializar(Application app)
        {
            app.Resources.ThemeDictionaries[ThemeVariant.Light] = Colores(false);
            app.Resources.ThemeDictionaries[ThemeVariant.Dark] = Colores(true);
            app.RequestedThemeVariant = Variante;
        }

        static ResourceDictionary Colores(bool oscuro)
        {
            Avalonia.Media.Color C(int r, int g, int b) => Avalonia.Media.Color.FromRgb((byte)r, (byte)g, (byte)b);
            IBrush B(Avalonia.Media.Color claro, Avalonia.Media.Color osc) => new SolidColorBrush(oscuro ? osc : claro);
            var d = new ResourceDictionary
            {
                ["Fondo"] = B(C(243, 245, 249), C(24, 26, 31)),
                ["Superficie"] = B(Colors.White, C(34, 37, 43)),
                ["Pie"] = B(C(243, 243, 243), C(28, 30, 35)),
                ["Borde"] = B(C(222, 227, 234), C(54, 58, 66)),
                ["Separador"] = B(C(236, 239, 243), C(46, 50, 57)),
                ["Texto"] = B(C(28, 33, 40), C(232, 234, 238)),
                ["Suave"] = B(C(96, 104, 116), C(160, 167, 178)),
                ["Enlace"] = B(C(0, 103, 192), C(110, 175, 255)),
                ["Destacado"] = B(C(0, 103, 192), C(110, 175, 255)),
                ["Exito"] = B(C(22, 128, 60), C(96, 204, 136)),
                ["Error"] = B(C(190, 40, 40), C(255, 122, 122)),
                ["Aviso"] = B(C(150, 95, 0), C(240, 190, 90)),
                ["ExitoFondo"] = B(C(225, 245, 232), C(30, 60, 42)),
                ["ErrorFondo"] = B(C(252, 231, 231), C(72, 34, 36)),
                ["AvisoFondo"] = B(C(253, 242, 214), C(66, 52, 24)),
                ["AvatarFondo"] = B(C(225, 238, 252), C(40, 62, 92)),
                ["CabeceraSubtitulo"] = B(C(220, 235, 250), C(190, 210, 232)),
                ["BotonCabecera"] = B(C(28, 122, 212), C(30, 78, 132)),
                ["Cabecera"] = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(oscuro ? C(12, 58, 108) : C(0, 96, 184), 0),
                        new GradientStop(oscuro ? C(20, 88, 156) : C(24, 132, 226), 1),
                    },
                },
            };
            return d;
        }

        // ---------- Enlazar propiedades a los colores del tema ----------

        public static T Enlazar<T>(T c, AvaloniaProperty propiedad, string clave) where T : Control
        {
            c.Bind(propiedad, c.GetResourceObservable(clave));
            return c;
        }

        public static T Texto<T>(T c, string clave) where T : Control => c switch
        {
            TextBlock => Enlazar(c, TextBlock.ForegroundProperty, clave),
            TemplatedControl => Enlazar(c, TemplatedControl.ForegroundProperty, clave),
            _ => c,
        };

        public static T Fondo<T>(T c, string clave) where T : Control => c switch
        {
            Border => Enlazar(c, Border.BackgroundProperty, clave),
            Panel => Enlazar(c, Panel.BackgroundProperty, clave),
            TemplatedControl => Enlazar(c, TemplatedControl.BackgroundProperty, clave),
            _ => c,
        };

        /// Botón "Tema: …" con su menú: Automático (como el sistema) / Claro / Oscuro
        public static Button CrearBoton()
        {
            var b = new Button
            {
                Content = "Tema: " + NombrePreferencia, Foreground = Brushes.White, Padding = new Thickness(12, 6),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, CornerRadius = new CornerRadius(4),
            };
            Fondo(b, "BotonCabecera");
            var menu = new MenuFlyout();
            foreach (var (modo, texto) in new[] { (ModoTema.Automatico, "Automático (como el sistema)"), (ModoTema.Claro, "Claro"), (ModoTema.Oscuro, "Oscuro") })
            {
                var item = new MenuItem { Header = texto, ToggleType = MenuItemToggleType.Radio, GroupName = "tema", IsChecked = preferencia == modo };
                item.Click += (s, e) => Preferencia = modo;
                menu.Items.Add(item);
            }
            menu.Opening += (s, e) =>
            {
                int i = 0;
                foreach (MenuItem it in menu.Items) it.IsChecked = (int)preferencia == i++;
            };
            b.Flyout = menu;
            void Actualizar() => b.Content = "Tema: " + NombrePreferencia;
            Cambio += Actualizar;
            b.DetachedFromVisualTree += (s, e) => Cambio -= Actualizar;
            return b;
        }
    }
}
