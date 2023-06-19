// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

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

  public static unsafe obs_module* ObsModule { get; private set; } = null;
  public static string ModuleName { get; private set; } = "xObsBeam";
  public static string ModulePath { get; private set; } = "";

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
    Log($"Unobserved task exception: {e.Exception.Message}\n{e.Exception.StackTrace}", ObsLogLevel.Error);
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
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
  public static unsafe void ToolsMenuItemClicked(void* private_data)
  {
    SettingsDialog.Show();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
  public static unsafe void FrontendEvent(obs_frontend_event eventName, void* private_data)
  {
    Log("FrontendEvent called", ObsLogLevel.Debug);
    switch (eventName)
    {
      case obs_frontend_event.OBS_FRONTEND_EVENT_FINISHED_LOADING:
        fixed (byte* menuItemText = "Beam"u8)
          ObsFrontendApi.obs_frontend_add_tools_menu_item((sbyte*)menuItemText, &ToolsMenuItemClicked, null);
        if (SettingsDialog.OutputEnabled)
          Output.Start();
        break;
      case obs_frontend_event.OBS_FRONTEND_EVENT_EXIT:
        if (Output.IsActive)
        {
          Log("OBS exiting, stopping output...", ObsLogLevel.Debug);
          Output.Stop();
        }
        break;
    }
  }
  #endregion Event handlers

  #region OBS module API methods
#pragma warning disable IDE1006
  [UnmanagedCallersOnly(EntryPoint = "obs_module_set_pointer", CallConvs = new[] { typeof(CallConvCdecl) })]
  public static unsafe void obs_module_set_pointer(obs_module* obsModulePointer)
  {
    Log("obs_module_set_pointer called", ObsLogLevel.Debug);
    ModuleName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name!;
    ObsModule = obsModulePointer;
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_ver", CallConvs = new[] { typeof(CallConvCdecl) })]
  public static uint obs_module_ver()
  {
    var major = (uint)Obs.Version.Major;
    var minor = (uint)Obs.Version.Minor;
    var patch = (uint)Obs.Version.Build;
    var version = (major << 24) | (minor << 16) | patch;
    return version;
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_load", CallConvs = new[] { typeof(CallConvCdecl) })]
  public static unsafe bool obs_module_load()
  {
    Log("Loading module...", ObsLogLevel.Debug);

    // register handlers for otherwise unhandled exceptions so that at least a log message is written
    AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionEventHandler;
    TaskScheduler.UnobservedTaskException += UnobservedTaskExceptionEventHandler;

    // remember where this module was loaded from
    ModulePath = Path.GetFullPath(Marshal.PtrToStringUTF8((IntPtr)Obs.obs_get_module_binary_path(ObsModule))!);

    var thisAssembly = System.Reflection.Assembly.GetExecutingAssembly();

    // configure library resolving for native libraries to additionally search the same directory as this module
    NativeLibrary.SetDllImportResolver(thisAssembly,
      (string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath) =>
      {
        Log($"Trying to load native library \"{libraryName}\" from additional path: {Path.GetDirectoryName(ModulePath)!}", ObsLogLevel.Debug);
        if (NativeLibrary.TryLoad(Path.Combine(Path.GetDirectoryName(ModulePath)!, libraryName), out nint handle)) // search current module directory
          return handle;

        if (libraryName == "turbojpeg")
        {
          Log($"Trying to load native library \"{libraryName}\" with additional name variant: libturbojpeg.so.0", ObsLogLevel.Debug);
          if (NativeLibrary.TryLoad("libturbojpeg.so.0", assembly, searchPath, out nint handle2))
            return handle2;
        }
        else if (libraryName == "QoirLib")
        {
          Log($"Trying to load native library \"{libraryName}\" with additional name variant: QoirLib.so.0", ObsLogLevel.Debug);
          if (NativeLibrary.TryLoad("QoirLib.so.0", assembly, searchPath, out nint handle2))
            return handle2;
        }
        else if (libraryName == "FpngeLib")
        {
          Log($"Trying to load native library \"{libraryName}\" with additional name variant: FpngeLib.so.0", ObsLogLevel.Debug);
          if (NativeLibrary.TryLoad("FpngeLib.so.0", assembly, searchPath, out nint handle2))
            return handle2;
        }

        return IntPtr.Zero; // fall back to default search paths and names
      }
    );

    ObsFrontendApi.obs_frontend_add_event_callback(&FrontendEvent, null);

    SettingsDialog.Register();

    Source.Register();

    Output.Register();
    Output.Create();

    Version version = thisAssembly.GetName().Version!;
    Log($"Version {version.Major}.{version.Minor}.{version.Build} loaded.", ObsLogLevel.Info);
    return true;
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_post_load", CallConvs = new[] { typeof(CallConvCdecl) })]
  public static void obs_module_post_load()
  {
    Log("obs_module_post_load called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_unload", CallConvs = new[] { typeof(CallConvCdecl) })]
  public static void obs_module_unload()
  {
    Log("obs_module_unload called", ObsLogLevel.Debug);
    SettingsDialog.Save();
    SettingsDialog.Dispose();
    Output.Dispose();
  }

  [UnmanagedCallersOnly(EntryPoint = "obs_module_set_locale", CallConvs = new[] { typeof(CallConvCdecl) })]
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

  [UnmanagedCallersOnly(EntryPoint = "obs_module_free_locale", CallConvs = new[] { typeof(CallConvCdecl) })]
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
