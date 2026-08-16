// SPDX-FileCopyrightText: © 2023-2026 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ObsInterop;

[assembly: DisableRuntimeMarshalling]
/*
All types used when interacting with native libraries (e.g. libobs through NetObsBindings or libjpeg-turbo) are defined in a way that are compatible between native and managed types.
E.g. bool would be 1 byte in managed/C# but 4 bytes in native/C++ and therfore any invoke definition with a bool function parameter or bool struct field would cause issues.
Fortunately NetObsBindings exposes them all as byte types and libjpeg-turbo doesn't use them at all (it uses int error code fields instead).

The DisableRuntimeMarshalling attribute is used to prevent the runtime from marshalling the types to their managed counterparts, which would be extra work with performance
const and in case of NativeAOT could introduce various issues.

Working this way has performance advantages, because no extra marshalling code has to be executed, neither at runtime nor precompiled as would be done by LibraryImport. Hence also
in .editorConfig the SYSLIB1054 warnings are suppressed, enabling us to stick with DllImport instead of LibraryImport, which wouldn't give us any advantages with the situation as
described above.

Note that as a result also Marshal.SizeOf should not be used, the .editorConfig is therefore configured to show warnings for CA1421 instead of just info messages.
*/

namespace xObsBeam;

public enum ObsLogLevel : int
{
  Error = ObsBase.LOG_ERROR,
  Warning = ObsBase.LOG_WARNING,
  Info = ObsBase.LOG_INFO,
  Debug = ObsBase.LOG_DEBUG
}

public static class Module
{
  const bool DebugLog = false; // set this to true and recompile to get debug messages from this plug-in only (unlike getting the full log spam when enabling debug log globally in OBS)
  const string DefaultLocale = "en-US";
  static string _locale = DefaultLocale;

#if MACOS
  // .NET's NamedPipeServerStream/NamedPipeClientStream use Unix domain sockets on macOS, locating
  // the socket file via TMPDIR (falling back to confstr(_CS_DARWIN_USER_TEMP_DIR)). TMPDIR differs
  // depending on how OBS was launched (Terminal inherits the shell's TMPDIR, Finder/Dock has none
  // and confstr may resolve to a different per-session sandbox dir), so two instances end up looking
  // in different directories and the pipe client never finds the server's socket (silent hang).
  // Parall (used to run multiple OBS instances) additionally overrides HOME per-instance, which would
  // redirect any HOME-relative path and can exceed the 104-char Unix domain socket path limit.
  //
  // Fix: force TMPDIR to a fixed shared directory that is not under HOME (so Parall doesn't redirect
  // it), short enough for the socket path limit, and independent of the launch-context-dependent
  // TMPDIR/confstr values. /tmp satisfies all of this; users can override via XOBSBEAM_PIPE_DIR.
  const string MacPipeDirEnvVar = "XOBSBEAM_PIPE_DIR";
  const string MacPipeTempDirDefault = "/tmp";

  /// <summary>
  /// Forces TMPDIR to a fixed shared directory so that NamedPipeServerStream and
  /// NamedPipeClientStream can find each other across independently-launched OBS instances.
  /// Must run before any pipe stream is created.
  /// </summary>
  static void NormalizeMacOSTempDir()
  {
    string? pipeTempDir = Environment.GetEnvironmentVariable(MacPipeDirEnvVar);
    if (string.IsNullOrEmpty(pipeTempDir))
      pipeTempDir = MacPipeTempDirDefault;

    try
    {
      Directory.CreateDirectory(pipeTempDir);
    }
    catch (Exception ex)
    {
      Log($"{ex.GetType().Name} creating pipe dir {pipeTempDir}: {ex.Message}, falling back to {MacPipeTempDirDefault}", ObsLogLevel.Warning);
      pipeTempDir = MacPipeTempDirDefault;
      try { Directory.CreateDirectory(pipeTempDir); } catch { }
    }

    var previousTmpdir = Environment.GetEnvironmentVariable("TMPDIR");
    Environment.SetEnvironmentVariable("TMPDIR", pipeTempDir + "/");
    if (!string.IsNullOrEmpty(previousTmpdir) && previousTmpdir != pipeTempDir + "/")
      Log($"TMPDIR overridden from {previousTmpdir} to {pipeTempDir}/ (named pipes require a shared directory on macOS; set {MacPipeDirEnvVar} to customize).", ObsLogLevel.Info);
    else
      Log($"TMPDIR set to {pipeTempDir}/ (named pipes will use this directory; set {MacPipeDirEnvVar} to customize).", ObsLogLevel.Debug);
  }
#endif

  public static unsafe obs_module* ObsModule { get; private set; } = null;
  public static string ModuleName { get; private set; } = "xObsBeam";
  public static string ModulePath { get; private set; } = "";
  public static string ModuleVersionString { get; private set; } = "0.0.0";

  // Homebrew library search paths for macOS (libjpeg-turbo is not bundled with the plugin)
  static readonly string[] HomebrewLibDirs = ["/opt/homebrew/lib", "/usr/local/lib"];

  static unsafe text_lookup* _textLookupModule = null;

  #region Helper methods

  public static unsafe void Log(string text, ObsLogLevel logLevel = ObsLogLevel.Info)
  {
    if (DebugLog && (logLevel == ObsLogLevel.Debug))
      logLevel = ObsLogLevel.Info;
    // need to escape %, otherwise they are treated as format items
    fixed (byte* logMessagePtr = Encoding.UTF8.GetBytes("[" + ModuleName + "] " + text.Replace("%", "%%")))
      ObsBase.blog((int)logLevel, (sbyte*)logMessagePtr);
  }

  public static void UnhandledExceptionEventHandler(object sender, UnhandledExceptionEventArgs e)
  {
    if (e.ExceptionObject is Exception ex)
    {
      Log($"Unhandled {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
      if (ex.InnerException is Exception innerEx)
        Log($"Unhandled inner {innerEx.GetType().Name}: {innerEx.Message}\n{innerEx.StackTrace}", ObsLogLevel.Error);
    }
    else
      Log($"Unknown unhandled exception object: {e.ExceptionObject}", ObsLogLevel.Error);
  }

  public static void UnobservedTaskExceptionEventHandler(object? sender, UnobservedTaskExceptionEventArgs e)
  {
    Log($"Unobserved task {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}", ObsLogLevel.Error);
  }

  public static byte[] ObsText(string identifier, params object[] args)
  {
    return Encoding.UTF8.GetBytes(string.Format(ObsTextString(identifier), args));
  }

  public static byte[] ObsText(string identifier)
  {
    return Encoding.UTF8.GetBytes(ObsTextString(identifier));
  }

  public static string ObsTextString(string identifier, params object[] args)
  {
    return string.Format(ObsTextString(identifier), args);
  }

  public static unsafe string ObsTextString(string identifier)
  {
    fixed (byte* lookupVal = Encoding.UTF8.GetBytes(identifier))
    {
      sbyte* lookupResult = null;
      ObsTextLookup.text_lookup_getstr(_textLookupModule, (sbyte*)lookupVal, &lookupResult);
      var resultString = Marshal.PtrToStringUTF8((IntPtr)lookupResult);
      if (string.IsNullOrEmpty(resultString))
        return "<MissingLocale:" + _locale + ":" + identifier + ">";
      else
        return resultString;
    }
  }

  public static unsafe string GetString(sbyte* obsString)
  {
    string managedString = Marshal.PtrToStringUTF8((IntPtr)obsString)!;
    ObsBmem.bfree(obsString);
    return managedString;
  }
  #endregion Helper methods

  #region Event handlers
  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void ToolsMenuItemClicked(void* private_data)
  {
    SettingsDialog.Show();
  }

  [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void FrontendEvent(obs_frontend_event eventName, void* private_data)
  {
    Log("FrontendEvent called", ObsLogLevel.Debug);
    switch (eventName)
    {
      case obs_frontend_event.OBS_FRONTEND_EVENT_FINISHED_LOADING:
        fixed (byte* menuItemText = "Beam Sender Output"u8)
          ObsFrontendApi.obs_frontend_add_tools_menu_item((sbyte*)menuItemText, &ToolsMenuItemClicked, null);
        if (SettingsDialog.Properties.Enabled)
          Output.Start();
        break;
      case obs_frontend_event.OBS_FRONTEND_EVENT_EXIT:
        Output.Shutdown();
        break;
    }
  }
  #endregion Event handlers

  #region OBS module API methods
#pragma warning disable IDE1006
  [UnmanagedCallersOnly(EntryPoint = "obs_module_set_pointer", CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void obs_module_set_pointer(obs_module* obsModulePointer)
  {
    Log("obs_module_set_pointer called", ObsLogLevel.Debug);
    ModuleName = Assembly.GetExecutingAssembly().GetName().Name!;
    ObsModule = obsModulePointer;
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_ver", CallConvs = [typeof(CallConvCdecl)])]
  public static uint obs_module_ver()
  {
    var major = (uint)Obs.Version.Major;
    var minor = (uint)Obs.Version.Minor;
    var patch = (uint)Obs.Version.Build;
    var version = (major << 24) | (minor << 16) | patch;
    return version;
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_load", CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe bool obs_module_load()
  {
    Log("Loading module...", ObsLogLevel.Debug);

#if MACOS
    // Must run before any NamedPipeServerStream/NamedPipeClientStream is created, so that both
    // the sender (server) and receiver (client) sides resolve the same Unix domain socket path.
    NormalizeMacOSTempDir();
#endif

    // register handlers for otherwise unhandled exceptions so that at least a log message is written
    AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionEventHandler;
    TaskScheduler.UnobservedTaskException += UnobservedTaskExceptionEventHandler;

    // initialize network interfaces list in background so that the first call to it doesn't take too long
    Task.Run(NetworkInterfaces.UpdateNetworkInterfaces);

    // remember where this module was loaded from
    ModulePath = Path.GetFullPath(Marshal.PtrToStringUTF8((IntPtr)Obs.obs_get_module_binary_path(ObsModule))!);

    var thisAssembly = Assembly.GetExecutingAssembly();

    // configure library resolving for native libraries to additionally search the same directory as this module
    NativeLibrary.SetDllImportResolver(thisAssembly,
      (string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
      {
        var moduleDirectory = Path.GetDirectoryName(ModulePath)!;
        Log($"Trying to load native library \"{libraryName}\" from additional path: {moduleDirectory}", ObsLogLevel.Debug);
        // search current module directory - on Windows the loader appends ".dll" to names without an extension, on Linux/macOS the "lib" prefix and ".so"/".dylib" suffix need to be added explicitly
        // (since .NET 10 the default search no longer includes the module directory for NativeAOT, see https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/10.0/native-library-search)
        if (NativeLibrary.TryLoad(Path.Combine(moduleDirectory, libraryName), out nint handle))
          return handle;
        if (!OperatingSystem.IsWindows())
        {
          var unixLibraryName = "lib" + libraryName + (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
          if (NativeLibrary.TryLoad(Path.Combine(moduleDirectory, unixLibraryName), out nint unixHandle))
            return unixHandle;
        }

        if (libraryName == "turbojpeg")
        {
          if (OperatingSystem.IsLinux())
          {
            Log($"Trying to load native library \"{libraryName}\" with additional name variant: libturbojpeg.so.0", ObsLogLevel.Debug);
            if (NativeLibrary.TryLoad("libturbojpeg.so.0", assembly, searchPath, out nint handle2))
              return handle2;
          }
          else if (OperatingSystem.IsMacOS())
          {
            // libjpeg-turbo is expected to be installed via Homebrew on macOS (not bundled with the plugin)
            // Homebrew installs to /opt/homebrew/lib on Apple Silicon and /usr/local/lib on Intel
            foreach (var homebrewLibDir in HomebrewLibDirs)
            {
              var homebrewPath = Path.Combine(homebrewLibDir, "libturbojpeg.dylib");
              Log($"Trying to load native library \"{libraryName}\" from Homebrew path: {homebrewPath}", ObsLogLevel.Debug);
              if (NativeLibrary.TryLoad(homebrewPath, out nint homebrewHandle))
                return homebrewHandle;
            }
          }
        }

        return IntPtr.Zero; // fall back to default search paths and names
      }
    );

    Output.Register();
    Output.Create();
    SettingsDialog.Register();
    ObsFrontendApi.obs_frontend_add_event_callback(&FrontendEvent, null);

    Source.Register();
    Filter.Register();

    var informationalVersion = thisAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    ModuleVersionString = (informationalVersion.Contains('+') ? informationalVersion.Split("+").First() : informationalVersion);
    var versionGitHash = (informationalVersion.Contains('+') ? informationalVersion.Split("+").Last() : "");
    Log($"Version {ModuleVersionString} loaded ({versionGitHash} built with .NET {Environment.Version}).", ObsLogLevel.Info);
    return true;
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_post_load", CallConvs = [typeof(CallConvCdecl)])]
  public static void obs_module_post_load()
  {
    Log("obs_module_post_load called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_unload", CallConvs = [typeof(CallConvCdecl)])]
  public static void obs_module_unload()
  {
    Log("obs_module_unload called", ObsLogLevel.Debug);
    SettingsDialog.Save();
    SettingsDialog.Dispose();
    Output.Dispose();
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_set_locale", CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void obs_module_set_locale(char* locale)
  {
    Log("obs_module_set_locale called", ObsLogLevel.Debug);
    var localeString = Marshal.PtrToStringUTF8((IntPtr)locale);
    if (!string.IsNullOrEmpty(localeString))
    {
      _locale = localeString;
      Log("Locale is set to: " + _locale, ObsLogLevel.Debug);
    }
    if (_textLookupModule != null)
      ObsTextLookup.text_lookup_destroy(_textLookupModule);
    fixed (byte* defaultLocale = Encoding.UTF8.GetBytes(DefaultLocale), currentLocale = Encoding.UTF8.GetBytes(_locale))
      _textLookupModule = Obs.obs_module_load_locale(ObsModule, (sbyte*)defaultLocale, (sbyte*)currentLocale);
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_free_locale", CallConvs = [typeof(CallConvCdecl)])]
  public static unsafe void obs_module_free_locale()
  {
    if (_textLookupModule != null)
      ObsTextLookup.text_lookup_destroy(_textLookupModule);
    _textLookupModule = null;
    Log("obs_module_free_locale called", ObsLogLevel.Debug);
  }
#pragma warning restore IDE1006
  #endregion OBS module API methods

}
