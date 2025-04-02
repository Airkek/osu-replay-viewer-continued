using osu.Framework;
using osu.Framework.Platform.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using osu.Framework.Platform;

namespace osu_replay_renderer_netcore.CustomHosts
{
    public static class CrossPlatform
    {
        public static IEnumerable<string> GetUserStoragePaths()
        {
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    break;

                case RuntimeInfo.Platform.Linux:
                case RuntimeInfo.Platform.macOS:
                    // https://github.com/ppy/osu-framework/blob/master/osu.Framework/Platform/Linux/LinuxGameHost.cs
                    string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                    if (!string.IsNullOrEmpty(xdg)) yield return xdg;
                    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share");
                    // foreach (string path in baseHost.UserStoragePaths) yield return path;
                    break;

                default: throw new InvalidOperationException($"Unknown platform: {Enum.GetName(typeof(RuntimeInfo.Platform), RuntimeInfo.OS)}");
            }
        }

        public static IWindow GetWindow(GraphicsSurfaceType preferredSurface)
        {
            string typeName;
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    typeName = "osu.Framework.Platform.Windows.SDL2WindowsWindow";
                    break;
                case RuntimeInfo.Platform.Linux:
                    typeName = "osu.Framework.Platform.Linux.SDL2LinuxWindow";
                    break;
                case RuntimeInfo.Platform.macOS:
                    typeName = "osu.Framework.Platform.MacOS.SDL2MacOSWindow";
                    break;
        
                default: throw new InvalidOperationException($"Unknown platform: {Enum.GetName(typeof(RuntimeInfo.Platform), RuntimeInfo.OS)}");
            }

            Type windowType = typeof(IWindow).Assembly.GetType(typeName);
            ConstructorInfo ctor = windowType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                [typeof(GraphicsSurfaceType), typeof(string)],
                null);
            if (ctor == null)
            {
                throw new MissingMethodException("Could not find the required constructor");
            }
            object instance = ctor.Invoke(new object[] { preferredSurface, "osureplayviewer" });
            return instance as IWindow;
        }
    }
}
