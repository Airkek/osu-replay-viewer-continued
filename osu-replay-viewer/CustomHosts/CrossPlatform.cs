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
                    // https://github.com/ppy/osu-framework/blob/master/osu.Framework/Platform/Windows/WindowsGameHost.cs
                    yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
                    break;

                case RuntimeInfo.Platform.Linux:
                    yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create); 
                    break;
                
                case RuntimeInfo.Platform.macOS:
                    yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
                    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                    break;

                default: throw new InvalidOperationException($"Unknown platform: {Enum.GetName(typeof(RuntimeInfo.Platform), RuntimeInfo.OS)}");
            }
        }

        public static IWindow GetWindow(GraphicsSurfaceType preferredSurface, string name)
        {
            string typeName;
            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    typeName = FrameworkEnvironment.UseSDL3 ? "osu.Framework.Platform.Windows.SDL3WindowsWindow" : "osu.Framework.Platform.Windows.SDL2WindowsWindow";
                    break;
                case RuntimeInfo.Platform.Linux:
                    typeName = FrameworkEnvironment.UseSDL3 ? "osu.Framework.Platform.Linux.SDL3LinuxWindow" : "osu.Framework.Platform.Linux.SDL2LinuxWindow";
                    break;
                case RuntimeInfo.Platform.macOS:
                    typeName = FrameworkEnvironment.UseSDL3 ? "osu.Framework.Platform.MacOS.SDL3MacOSWindow" : "osu.Framework.Platform.MacOS.SDL2MacOSWindow";
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
            object instance = ctor.Invoke(new object[] { preferredSurface, name });
            return instance as IWindow;
        }
    }
}
