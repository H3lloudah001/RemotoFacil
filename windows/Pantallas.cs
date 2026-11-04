using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RemotoFacil
{
    class Monitor
    {
        public string Ruta;   // monitorDevicePath: identifica la pantalla aunque cambie de número
        public string Nombre;
        public string Gdi;    // \\.\DISPLAY1, para cruzarlo con Screen.AllScreens
        public int Ancho, Alto, X, Y;
        public bool Principal => X == 0 && Y == 0;
    }

    /// <summary>
    /// Apaga y vuelve a encender pantallas con la API CCD de Windows (QueryDisplayConfig/SetDisplayConfig).
    /// Es el equivalente en Windows del script modo-cine.
    /// </summary>
    static class Pantallas
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        const uint SDC_TOPOLOGY_EXTEND = 0x4;
        const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        const uint SDC_APPLY = 0x80;
        const uint SDC_SAVE_TO_DATABASE = 0x200;
        const uint SDC_ALLOW_CHANGES = 0x400;
        const uint PATH_ACTIVE = 0x1;
        const uint MODE_SOURCE = 1;
        const uint IDX_INVALID = 0xFFFFFFFF;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const uint OUTPUT_INTERNAL = 0x80000000, OUTPUT_EDP = 11, OUTPUT_UDI_EMBEDDED = 13;

        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct Racional { public uint Num, Den; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathSource { public LUID Adaptador; public uint Id; public uint ModeIdx; public uint Estado; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public LUID Adaptador; public uint Id; public uint ModeIdx; public uint Salida; public uint Rotacion;
            public uint Escalado; public Racional Refresco; public uint Barrido; public int Disponible; public uint Estado;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        struct ModeInfo
        {
            [FieldOffset(0)] public uint Tipo;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public LUID Adaptador;
            // Solo usamos la variante "source mode" de la unión
            [FieldOffset(16)] public uint Ancho;
            [FieldOffset(20)] public uint Alto;
            [FieldOffset(24)] public uint FormatoPixel;
            [FieldOffset(28)] public int X;
            [FieldOffset(32)] public int Y;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Cabecera { public uint Tipo; public uint Tamano; public LUID Adaptador; public uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NombreTarget
        {
            public Cabecera C; public uint Flags; public uint Salida; public ushort EdidFabricante; public ushort EdidProducto;
            public uint Conector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Nombre;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Ruta;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NombreSource
        {
            public Cabecera C;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi;
        }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topologia);
        [DllImport("user32.dll")]
        static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[] paths, uint numModes, [In] ModeInfo[] modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref NombreTarget req);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref NombreSource req);

        static void Comprobar(int r, string que)
        {
            if (r != 0) throw new InvalidOperationException($"{que} falló (error {r})");
        }

        static (PathInfo[] paths, ModeInfo[] modes) Consultar()
        {
            for (int intento = 0; intento < 5; intento++)
            {
                Comprobar(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm), "GetDisplayConfigBufferSizes");
                var p = new PathInfo[np];
                var m = new ModeInfo[nm];
                int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, p, ref nm, m, IntPtr.Zero);
                if (r == ERROR_INSUFFICIENT_BUFFER) continue;
                Comprobar(r, "QueryDisplayConfig");
                Array.Resize(ref p, (int)np);
                Array.Resize(ref m, (int)nm);
                return (p, m);
            }
            throw new InvalidOperationException("No se pudo leer la configuración de pantallas");
        }

        static NombreTarget LeerTarget(PathInfo p)
        {
            var t = new NombreTarget();
            t.C.Tipo = 2; // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
            t.C.Tamano = (uint)Marshal.SizeOf<NombreTarget>();
            t.C.Adaptador = p.Target.Adaptador;
            t.C.Id = p.Target.Id;
            DisplayConfigGetDeviceInfo(ref t);
            return t;
        }

        static string LeerGdi(PathInfo p)
        {
            var s = new NombreSource();
            s.C.Tipo = 1; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
            s.C.Tamano = (uint)Marshal.SizeOf<NombreSource>();
            s.C.Adaptador = p.Source.Adaptador;
            s.C.Id = p.Source.Id;
            return DisplayConfigGetDeviceInfo(ref s) == 0 ? s.Gdi : "";
        }

        public static List<Monitor> Listar()
        {
            var (paths, modes) = Consultar();
            var lista = new List<Monitor>();
            foreach (var p in paths)
            {
                if ((p.Flags & PATH_ACTIVE) == 0) continue;
                var t = LeerTarget(p);
                bool integrada = t.Salida == OUTPUT_INTERNAL || t.Salida == OUTPUT_EDP || t.Salida == OUTPUT_UDI_EMBEDDED;
                var m = new Monitor
                {
                    Ruta = t.Ruta ?? "",
                    Nombre = !string.IsNullOrWhiteSpace(t.Nombre) ? t.Nombre : integrada ? "Pantalla del portátil" : "Pantalla genérica",
                    Gdi = LeerGdi(p),
                };
                if (p.Source.ModeIdx < modes.Length && modes[p.Source.ModeIdx].Tipo == MODE_SOURCE)
                {
                    var sm = modes[p.Source.ModeIdx];
                    m.Ancho = (int)sm.Ancho; m.Alto = (int)sm.Alto; m.X = sm.X; m.Y = sm.Y;
                }
                lista.Add(m);
            }
            return lista.OrderBy(x => x.X).ThenBy(x => x.Y).ToList();
        }

        // ---------- Apagar / restaurar (lo llama Sunshine al empezar y terminar la sesión) ----------

        public static void ApagarSegunConfiguracion()
        {
            if (!File.Exists(Rutas.ArchivoPantallas))
            {
                Registro.Escribir("No hay pantallas configuradas para apagar.");
                return;
            }
            var rutas = new HashSet<string>(
                File.ReadAllLines(Rutas.ArchivoPantallas)
                    .Select(l => l.Split('\t')[0].Trim())
                    .Where(l => l.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            if (rutas.Count == 0) return;

            var (paths, modes) = Consultar();
            var quedan = new List<PathInfo>();
            int apagadas = 0;
            foreach (var p in paths)
            {
                if ((p.Flags & PATH_ACTIVE) == 0) continue;
                var t = LeerTarget(p);
                if (rutas.Contains(t.Ruta ?? "")) { apagadas++; Registro.Escribir("Apagando: " + t.Nombre); }
                else quedan.Add(p);
            }
            if (apagadas == 0) { Registro.Escribir("Las pantallas elegidas ya están apagadas o no están conectadas."); return; }
            if (quedan.Count == 0) { Registro.Escribir("No se apaga nada: se quedarían todas las pantallas apagadas."); return; }

            GuardarEstado(paths, modes);

            // Primer intento: conservar la colocación. Segundo: juntarlas en fila por si Windows rechaza huecos.
            foreach (bool compactar in new[] { false, true })
            {
                var (np, nm) = Reducir(quedan, modes, compactar);
                int r = SetDisplayConfig((uint)np.Length, np, (uint)nm.Length, nm, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
                if (r == 0) { Registro.Escribir($"Pantallas apagadas: {apagadas} (compactar={compactar})"); return; }
                Registro.Escribir($"SetDisplayConfig falló con error {r} (compactar={compactar})");
            }
        }

        /// Deja solo los paths que quedan, con su propio array de modos, y asegura que alguno quede como principal en (0,0).
        static (PathInfo[], ModeInfo[]) Reducir(List<PathInfo> quedan, ModeInfo[] modes, bool compactar)
        {
            var nuevos = new List<ModeInfo>();
            var mapa = new Dictionary<uint, uint>();
            uint Reasignar(uint idx)
            {
                if (idx == IDX_INVALID || idx >= modes.Length) return IDX_INVALID;
                if (!mapa.TryGetValue(idx, out uint n)) { n = (uint)nuevos.Count; nuevos.Add(modes[idx]); mapa[idx] = n; }
                return n;
            }
            var paths = quedan.Select(p =>
            {
                p.Source.ModeIdx = Reasignar(p.Source.ModeIdx);
                p.Target.ModeIdx = Reasignar(p.Target.ModeIdx);
                return p;
            }).ToArray();

            var fuentes = paths.Select(p => p.Source.ModeIdx).Where(i => i != IDX_INVALID).Distinct()
                               .OrderBy(i => nuevos[(int)i].X).ToList();
            if (fuentes.Count == 0) return (paths, nuevos.ToArray());

            // La principal es la que está en (0,0). Si sigue encendida, que lo siga siendo.
            var principal = fuentes.Where(i => nuevos[(int)i].X == 0 && nuevos[(int)i].Y == 0).Select(i => (uint?)i).FirstOrDefault();

            if (compactar)
            {
                int x = 0;
                foreach (var i in fuentes)
                {
                    var m = nuevos[(int)i];
                    m.X = x; m.Y = 0; x += (int)m.Ancho;
                    nuevos[(int)i] = m;
                }
            }
            // Desplazar todo para que la principal (o, si se apagó, la primera que queda) esté en (0,0)
            var referencia = nuevos[(int)(principal ?? fuentes[0])];
            int dx = referencia.X, dy = referencia.Y;
            foreach (var i in fuentes)
            {
                var m = nuevos[(int)i];
                m.X -= dx; m.Y -= dy;
                nuevos[(int)i] = m;
            }
            return (paths, nuevos.ToArray());
        }

        public static void Restaurar(bool forzar)
        {
            if (File.Exists(Rutas.ArchivoEstado))
            {
                try
                {
                    var (p, m) = LeerEstado();
                    int r = SetDisplayConfig((uint)p.Length, p, (uint)m.Length, m,
                        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
                    if (r == 0)
                    {
                        File.Delete(Rutas.ArchivoEstado);
                        Registro.Escribir("Pantallas restauradas.");
                        return;
                    }
                    Registro.Escribir($"No se pudo aplicar la configuración guardada (error {r}); se usará 'Extender'.");
                }
                catch (Exception e) { Registro.Escribir("Error leyendo el estado guardado: " + e.Message); }
            }
            else if (!forzar)
            {
                Registro.Escribir("No había nada que restaurar.");
                return;
            }

            int r2 = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
            Registro.Escribir(r2 == 0 ? "Pantallas restauradas en modo Extender." : $"Extender falló (error {r2}).");
            try { File.Delete(Rutas.ArchivoEstado); } catch { }
        }

        // ---------- Guardado del estado original ----------

        static void GuardarEstado(PathInfo[] paths, ModeInfo[] modes)
        {
            if (File.Exists(Rutas.ArchivoEstado))
            {
                // Si quedó un estado de una sesión que no se cerró bien, lo conservamos mientras siga habiendo
                // menos pantallas encendidas que entonces (es decir, mientras sigan apagadas).
                try
                {
                    var (viejo, _) = LeerEstado();
                    if (paths.Length < viejo.Length) { Registro.Escribir("Se conserva el estado guardado anterior."); return; }
                }
                catch { }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Rutas.ArchivoEstado));
            using (var w = new BinaryWriter(File.Create(Rutas.ArchivoEstado)))
            {
                w.Write(0x52464131); // "RFA1"
                w.Write(paths.Length);
                w.Write(modes.Length);
                w.Write(ABytes(paths));
                w.Write(ABytes(modes));
            }
        }

        static (PathInfo[], ModeInfo[]) LeerEstado()
        {
            using (var r = new BinaryReader(File.OpenRead(Rutas.ArchivoEstado)))
            {
                if (r.ReadInt32() != 0x52464131) throw new InvalidDataException("formato desconocido");
                int np = r.ReadInt32(), nm = r.ReadInt32();
                var p = DesdeBytes<PathInfo>(r.ReadBytes(np * Marshal.SizeOf<PathInfo>()), np);
                var m = DesdeBytes<ModeInfo>(r.ReadBytes(nm * Marshal.SizeOf<ModeInfo>()), nm);
                return (p, m);
            }
        }

        static byte[] ABytes<T>(T[] arr) where T : struct
        {
            var buf = new byte[Marshal.SizeOf<T>() * arr.Length];
            var h = GCHandle.Alloc(arr, GCHandleType.Pinned);
            try { Marshal.Copy(h.AddrOfPinnedObject(), buf, 0, buf.Length); }
            finally { h.Free(); }
            return buf;
        }

        static T[] DesdeBytes<T>(byte[] buf, int n) where T : struct
        {
            if (buf.Length != Marshal.SizeOf<T>() * n) throw new InvalidDataException("archivo de estado incompleto");
            var arr = new T[n];
            var h = GCHandle.Alloc(arr, GCHandleType.Pinned);
            try { Marshal.Copy(buf, 0, h.AddrOfPinnedObject(), buf.Length); }
            finally { h.Free(); }
            return arr;
        }
    }
}
