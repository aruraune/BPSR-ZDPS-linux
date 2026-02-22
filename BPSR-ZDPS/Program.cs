using BPSR_ZDPS.Windows;
using Hexa.NET.GLFW;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.OpenGL3;
using Hexa.NET.ImGui.Backends.GLFW;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BPSR_ZDPS.DataTypes;
using GLFWwindowPtr = Hexa.NET.GLFW.GLFWwindowPtr;
using Hexa.NET.ImPlot;

namespace BPSR_ZDPS
{
    internal class Program
    {
        private static MainWindow mainWindow;
        private static GLFWwindowPtr window;
        private static OpenGLManager manager;

        static void Main(string[] args)
        {            
            Settings.Load();

            var logBuilder = new LoggerConfiguration();
            logBuilder = logBuilder.MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Debug();

            logBuilder.WriteTo.Console(Serilog.Events.LogEventLevel.Error);

            if (Settings.Instance.LogToFile)
            {
                if (File.Exists("ZDPS_log.txt"))
                {
                    File.Copy("ZDPS_log.txt", "ZDPS_log_last_run.txt", true);
                    File.Delete("ZDPS_log.txt");
                }

                logBuilder = logBuilder.WriteTo.File("ZDPS_log.txt");
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            }

            Log.Logger = logBuilder.CreateLogger();

            Log.Information($"Starting ZDPS v{Utils.AppVersion}");

            DB.Init();

            GLFW.Init();

            // Set OpenGL context hints
            GLFW.WindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.WindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 3);
            GLFW.WindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            
            // Linux compatibility - required for macOS and works on Linux
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                GLFW.WindowHint(GLFW.GLFW_OPENGL_FORWARD_COMPAT, 1);
            }

            GLFW.WindowHint(GLFW.GLFW_FOCUSED, 1);    // Make window focused on start
            GLFW.WindowHint(GLFW.GLFW_RESIZABLE, 1);  // Make window resizable
            GLFW.WindowHint(GLFW.GLFW_VISIBLE, 0); // Start window hidden so it can be nicely positioned first
            
            // On Windows, use transparent framebuffer for overlay-style rendering
            // On Linux, use opaque window for normal desktop app behavior
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                GLFW.WindowHint(GLFW.GLFW_TRANSPARENT_FRAMEBUFFER, 1);
                GLFW.WindowHint(GLFW.GLFW_DECORATED, 0); // Borderless on Windows for overlay
            }
            // Linux gets default decorated window (normal title bar and borders)

            // TODO: Load these values from a settings file
            int windowWidth = 1040;  // 800 * 1.30
            int windowHeight = 780;  // 600 * 1.30

            window = GLFW.CreateWindow(windowWidth, windowHeight, "ZDPS", null, null);
            if (window.IsNull)
            {
                Console.WriteLine("Failed to create GLFW window.");
                GLFW.Terminate();
                return;
            }

            Assembly assembly = Assembly.GetExecutingAssembly();
            string iconAssemblyPath = "BPSR_ZDPS.Resources.MainWindowIcon.png";
            using (var iconStream = assembly.GetManifestResourceStream(iconAssemblyPath))
            {
                if (iconStream != null)
                {
                    SetWindowIcon(window, iconStream);
                }
            }

            // Center window initially
            var glfwMonitor = GLFW.GetPrimaryMonitor();
            var glfwVidMode = GLFW.GetVideoMode(glfwMonitor);
            GLFW.SetWindowPos(window, (glfwVidMode.Width - windowWidth) / 2, (glfwVidMode.Height - windowHeight) / 2);

            Log.Debug($"Primary Monitor Refresh Rate = {glfwVidMode.RefreshRate}hz");
            if (Settings.Instance.LowPerformanceMode)
            {
                Log.Debug($"Low Performance Mode is Enabled");
            }

            // Show the main window on Linux to ensure at least one window is visible
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                GLFW.ShowWindow(window);
            }

            manager = new OpenGLManager(window, false);

            HelperMethods.GLFWwindow = window;

            var guiContext = ImGui.CreateContext();
            ImGui.SetCurrentContext(guiContext);
            ImPlot.SetImGuiContext(guiContext);
            
            var guiPlotContext = ImPlot.CreateContext();
            ImPlot.SetCurrentContext(guiPlotContext);

            // Setup ImGui config.
            var io = ImGui.GetIO();

            // Disable imgui.ini file writing
            unsafe
            {
                io.IniFilename = null;
            }

            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;     // Enable Keyboard Controls
            if (Settings.Instance.AllowGamepadNavigationInputInZDPS)
            {
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;  // Enable Gamepad Controls
            }
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;         // Enable Docking
            
            // On Linux, keep it simple - single window with docking, no separate viewports
            // All windows will dock as tabs in the main window
            //if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;       // Enable Multi-Viewport / Platform Windows
            }
            
            io.ConfigViewportsNoAutoMerge = true; // If this is false, putting an ImGui window on top of an GLFW window will dock into it even if it's not shown
            io.ConfigViewportsNoTaskBarIcon = false;

            Log.Error("DEBUG: About to init GLFW backend...");

            ImGuiImplGLFW.SetCurrentContext(guiContext);
            Log.Error("DEBUG: GLFW context set, about to init GLFW for OpenGL...");
            if (!ImGuiImplGLFW.InitForOpenGL(Unsafe.BitCast<GLFWwindowPtr, Hexa.NET.ImGui.Backends.GLFW.GLFWwindowPtr>(window), true))
            {
                Console.WriteLine("Failed to init ImGui Impl GLFW");
                GLFW.Terminate();
                return;
            }
            Log.Error("DEBUG: GLFW backend initialized, about to init OpenGL3 backend...");

            ImGuiImplOpenGL3.SetCurrentContext(guiContext);
            Log.Error("DEBUG: OpenGL3 context set, about to call Init...");
            if (!ImGuiImplOpenGL3.Init("#version 330"))
            {
                Console.WriteLine("Failed to init ImGui Impl OpenGL3");
                GLFW.Terminate();
                return;
            }
            
            Log.Error("DEBUG: OpenGL3 backend initialized, now loading fonts...");
            LoadFonts();

            // Setup resizing.
            unsafe
            {
                GLFW.SetFramebufferSizeCallback(window, Window_Resized_Callback);
            }

            InitWindows();

            Theme.VSDarkTheme();

            // Windows 11 does not properly update the task bar icon when instructed to, so you have to tell it multiple times
            using (var iconStream = assembly.GetManifestResourceStream(iconAssemblyPath))
            {
                if (iconStream != null)
                {
                    SetWindowIcon(window, iconStream);
                }
            }

            // Initialize ImageHelper with OpenGL manager
            ImageHelper.SetDeviceManager(manager);
            
            // TODO: Update OffscreenImGuiRenderer for OpenGL (report generation feature)
            // For now, commenting out D3D11-specific OffscreenImGuiRenderer
            // unsafe
            // {
            //     OffscreenImGuiRenderer.Initialize(manager, guiContext);
            // }
            
            ImageArchive.LoadBaseImages();

            // Main loop
            while (GLFW.WindowShouldClose(window) == 0)
            {
                // Poll for and process events
                GLFW.PollEvents();

                //var isMouseDragging = ImGui.IsMouseDragging(ImGuiMouseButton.Left);
                //GLFW.SwapInterval(isMouseDragging ? 0 : 1);

                // Note: This check is for the GLFW base window, not the 'MainWindow' or anything else
                var isMinimized = GLFW.GetWindowAttrib(window, GLFW.GLFW_ICONIFIED);
                if (isMinimized >= 1)
                {
                    System.Threading.Thread.Sleep(10);
                    continue;
                }

                if (Settings.Instance.LowPerformanceMode)
                {
                    System.Threading.Thread.Sleep(10);
                }

                ImGuiImplOpenGL3.NewFrame();
                ImGuiImplGLFW.NewFrame();
                ImGui.NewFrame();

                RenderWindowList();
                //ImGui.ShowDemoWindow();
                //ImPlot.ShowDemoWindow();

                ImGui.Render();
                ImGui.EndFrame();

                manager.SetTarget();

                // On Linux with decorated window, use solid background to prevent flashing
                // On Windows, use transparent background for overlay mode
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    manager.Clear(new(0.15f, 0.15f, 0.15f, 1.0f)); // Solid dark gray
                }
                else
                {
                    manager.Clear(new(0, 0, 0, 0.0f)); // Transparent
                }

                ImGuiImplOpenGL3.RenderDrawData(ImGui.GetDrawData());

                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
                {
                    ImGui.UpdatePlatformWindows();
                    ImGui.RenderPlatformWindowsDefault();
                }

                // We can present without vsync to run at double the normal framerate to have input be more responive
                // Double of framerate is controlled in the D3D11Manager by the SwapChain's BufferCount
                //manager.Present((uint)isMouseDragging ? 0 : 1, 0);

                if (!Settings.Instance.LowPerformanceMode)
                {
                    manager.Present(Settings.Instance.FixedFramerateScale, 0);
                }
                else
                {
                manager.Present(1, 0);
                }

                if (HelperMethods.DeferredImGuiRenderAction != null)
                {
                    HelperMethods.DeferredImGuiRenderAction.Invoke();
                    HelperMethods.DeferredImGuiRenderAction = null;
                }
            }

            Log.Information("ZDPS is beginning exit process.");

            // Stop capturing new data to allow our current states to be their final states
            MessageManager.StopCapturing();

            // Save the current encounter to the database before exiting
            if (EncounterManager.Current != null)
            {
                EncounterManager.ShutdownManager();
            }

            DB.CloseAndSave();
            Settings.Save();

            HotKeyManager.UnregisterAllHotKeys();
            //HotKeyManager.UnregisterHookProc();

            System.Diagnostics.Stopwatch writingTimeout = new();
            writingTimeout.Start();
            while (EntityCache.Instance.IsWritingFile)
            {
                // Spin until writing has finished
                System.Threading.Thread.Sleep(10);

                if (writingTimeout.IsRunning && writingTimeout.Elapsed.TotalSeconds >= 6)
                {
                    Log.Warning("EntityCache writing has taken too long during exit process! Forcing exit (this may corrupt the cache).");
                    break;
                }
            }
            writingTimeout.Stop();

            ImGuiImplOpenGL3.Shutdown();
            ImGuiImplOpenGL3.SetCurrentContext(null);
            ImGuiImplGLFW.Shutdown();
            ImGuiImplGLFW.SetCurrentContext(null);
            ImPlot.DestroyContext();
            ImGui.DestroyContext();
            manager.Dispose();

            // Clean up and terminate GLFW
            GLFW.DestroyWindow(window);
            GLFW.Terminate();

            Log.Information("ZDPS has successfully terminated all contexts. Performing final retention policy checks.");

            if (Settings.Instance.UseDatabaseForEncounterHistory && Settings.Instance.DatabaseRetentionPolicyDays > 0)
            {
                DB.ClearOldEncounters(Settings.Instance.DatabaseRetentionPolicyDays);
            }

            if (Settings.Instance.SaveEncounterReportToFile && Settings.Instance.ReportFileRetentionPolicyDays > 0)
            {
                var reportFiles = Directory.EnumerateFiles(Path.Combine("Reports"));
                foreach (var file in reportFiles)
                {
                    var fileExtension = Path.GetExtension(file);
                    if (!fileExtension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileNameWithoutExtension(file);

                    if (!fileName.StartsWith("Report_"))
                    {
                        continue;
                    }

                    var strippedName = fileName.Substring(7);

                    if (DateTime.TryParseExact(strippedName, "yyyy-MM-dd_HH-mm-ss-ff", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                    {
                        // File name format matches being one of our generated Reports, it's finally safe to now attempt deleting it per the retention policy setting
                        var difference = DateTime.Now.Subtract(parsed);

                        if (difference.TotalDays > Settings.Instance.ReportFileRetentionPolicyDays)
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch (IOException ioException)
                            {
                                Log.Error($"IO Error deleting Report file {file} per Retention Policy (Days = {Settings.Instance.ReportFileRetentionPolicyDays}, Difference = {difference}). Error: {ioException.Message}");
                            }
                            catch (UnauthorizedAccessException uaException)
                            {
                                Log.Error($"Unauthorized Access Error deleting Report file {file} per Retention Policy (Days = {Settings.Instance.ReportFileRetentionPolicyDays}, Difference = {difference}). Error: {uaException.Message}");
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"Error deleting Report file {file} per Retention Policy (Days = {Settings.Instance.ReportFileRetentionPolicyDays}, Difference = {difference}). Error: {ex.Message}");
                            }
                        }
                    }
                }
            }

            Log.Information("ZDPS has cleanly exited.");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Log.Error($"Unhandled Exception:\n{ex?.Message}\nStack Trace:\n{ex?.StackTrace}");
        }

        static unsafe void Window_Resized_Callback(Hexa.NET.GLFW.GLFWwindow* window, int width, int height)
        {
            manager.Resize(width, height);
        }

        static unsafe void LoadFonts()
        {
            Log.Error("DEBUG: LoadFonts - Starting");
            var io = ImGui.GetIO();
            Log.Error("DEBUG: LoadFonts - Got ImGui IO");
            
            // Try to load system UI font (platform-specific)
            ImFontPtr? segoe = null;
            Log.Error("DEBUG: LoadFonts - About to check platform");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    segoe = io.Fonts.AddFontFromFileTTF(@"C:\Windows\Fonts\segoeui.ttf", 18.0f);
                }
                catch
                {
                    Log.Warning("Failed to load Segoe UI font from Windows");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Log.Error("DEBUG: LoadFonts - On Linux platform");
                // Try common Linux fonts
                string[] linuxFonts = new[]
                {
                    "/usr/share/fonts/TTF/segoeui.ttf",
                };
                Log.Error("DEBUG: LoadFonts - Searching for Linux fonts...");
                
                foreach (var fontPath in linuxFonts)
                {
                    Log.Error($"DEBUG: LoadFonts - Checking font path: {fontPath}");
                    if (File.Exists(fontPath))
                    {
                        Log.Error($"DEBUG: LoadFonts - Found font, attempting to load: {fontPath}");
                        try
                        {
                            segoe = io.Fonts.AddFontFromFileTTF(fontPath, 18.0f);
                            Log.Information($"Loaded system font: {fontPath}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"DEBUG: LoadFonts - Failed to load font: {fontPath}");
                            continue;
                        }
                    }
                }
            }
            
            Log.Error("DEBUG: LoadFonts - Finished platform-specific font loading");
            
            // Fallback to default if no system font loaded
            if (segoe == null)
            {
                Log.Error("DEBUG: LoadFonts - No system font found on Linux, using built-in ImGui font");
                // On Linux, just use the default font that ImGui already has loaded
                // ImGui automatically loads a default font when the context is created
                segoe = io.Fonts.Fonts[0]; // Get the first (default) font
                Log.Error("DEBUG: LoadFonts - Using ImGui's built-in default font");
            }
            
            Log.Error($"DEBUG: LoadFonts - About to add Segoe to HelperMethods.Fonts (segoe is null: {segoe == null})");
            HelperMethods.Fonts.Add("Segoe", segoe.Value);

            Log.Error("DEBUG: LoadFonts - Segoe font added, loading multi-language fonts...");

            // Merging additional fonts into Segoe for multi-language support

            // Japanese character supporting font (this is a bit heavy to load into memory - 5MB)
            //ff = new FontFile("BPSR_ZDPS.Fonts.fot-seuratpron-m.otf");
            var ff = new FontFile("BPSR_ZDPS.Fonts.fot-seuratpron-m.otf", new GlyphRange(0x3000, 0x303F));
            var res = ff.BindToImGui(18.0f, true);
            ff.Dispose();

            // Chinese character supporting font (this is very heavy to load into memory - 16MB)
            ff = new FontFile("BPSR_ZDPS.Fonts.SourceHanSansSC-Regular.otf", new GlyphRange(0x4E00, 0x9FFF));
            res = ff.BindToImGui(18.0f, true);
            ff.Dispose();

            // Korean character supporting font
            ff = new FontFile("BPSR_ZDPS.Fonts.NotoSansKR-Regular.ttf", new GlyphRange(0x4E00, 0x9FFF));
            res = ff.BindToImGui(18.0f, true);
            ff.Dispose();

            Log.Error("DEBUG: LoadFonts - Multi-language fonts loaded, setting default font...");

            // Setting Segoe to be the default application font (though the other fonts will be used if their glyphs are required)
            // Note: Not calling AddFontDefault again as we already have Segoe as default

            Log.Error("DEBUG: LoadFonts - Adding Segoe Bold...");

            // Note: Segoe-Bold will not support multi-language when it's used
            ImFontPtr? segoeBold = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    segoeBold = io.Fonts.AddFontFromFileTTF(@"C:\Windows\Fonts\segoeuib.ttf", 18.0f);
                }
                catch
                {
                    segoeBold = segoe;  // Fallback to regular font
                }
            }
            else
            {
                segoeBold = segoe;  // On Linux, use the same font as regular
            }
            HelperMethods.Fonts.Add("Segoe-Bold", segoeBold.Value);

            ff = new FontFile("BPSR_ZDPS.Fonts.FAS.ttf", new GlyphRange(0x0021, 0xF8FF));
            res = ff.BindToImGui(18.0f);
            HelperMethods.Fonts.Add("FASIcons", res);
            ff.Dispose();

            // Windows 11 doesn't actually have this anymore so we can't rely on the system, we have to embed it
            //HelperMethods.Fonts.Add("Cascadia-Mono", io.Fonts.AddFontFromFileTTF(@"C:\Windows\Fonts\CascadiaMono.ttf", 18.0f));
            ff = new FontFile("BPSR_ZDPS.Fonts.CascadiaMono.ttf");
            res = ff.BindToImGui(18.0f);
            HelperMethods.Fonts.Add("Cascadia-Mono", res);
            ff.Dispose();

            // The below fonts are being merged into Cascadia-Mono

            // Japanese character supporting monospace font
            ff = new FontFile("BPSR_ZDPS.Fonts.CascadiaNextJP.wght.ttf");
            res = ff.BindToImGui(18.0f, true);
            ff.Dispose();

            // Chinese Simplified character supporting monospace font
            ff = new FontFile("BPSR_ZDPS.Fonts.CascadiaNextSC.wght.ttf");
            res = ff.BindToImGui(18.0f, true);
            ff.Dispose();

            // Chinese Traditional character supporting monospace font
            ff = new FontFile("BPSR_ZDPS.Fonts.CascadiaNextTC.wght.ttf");
            res = ff.BindToImGui(18.0f, true);
            ff.Dispose();

            // Korean character supporting monospace font
            ff = new FontFile("BPSR_ZDPS.Fonts.NanumGothicCoding.ttf");
            res = ff.BindToImGui(18.0f, true);
            ff.Dispose();
            
            Log.Error("DEBUG: LoadFonts - Completed successfully");
        }

        static unsafe void SetWindowIcon(GLFWwindowPtr window, string IconFilePath)
        {
            using (var stream = File.Open(IconFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SetWindowIcon(window, stream);
            }
        }

        static unsafe void SetWindowIcon(GLFWwindowPtr window, Stream IconFileStream)
        {
            using (Image<Rgba32> image = Image.Load<Rgba32>(IconFileStream))
            {
                // Convert image data to byte array
                byte[] pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);

                // Allocate unmanaged memory for pixels
                IntPtr pixelsPtr = Marshal.AllocHGlobal(pixels.Length);
                Marshal.Copy(pixels, 0, pixelsPtr, pixels.Length);

                // Create GLFWimage structure
                GLFWimage iconImage = new GLFWimage
                {
                    Width = image.Width,
                    Height = image.Height,
                    Pixels = (byte*)pixelsPtr
                };

                // Create an array for GLFWimage structures (though we only have one currently)
                GLFWimage[] images = new GLFWimage[] { iconImage };

                // Pin the array to prevent garbage collection during the call
                GCHandle handle = GCHandle.Alloc(images, GCHandleType.Pinned);
                IntPtr imagesPtr = handle.AddrOfPinnedObject();

                GLFW.SetWindowIcon(window, 1, (GLFWimage*)imagesPtr);
                handle.Free();
            }
        }

        static void InitWindows()
        {
            mainWindow = new MainWindow();
        }

        static void RenderWindowList()
        {
            ImGui.PushStyleColor(ImGuiCol.ResizeGrip, 0);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, 0);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, 0);
            mainWindow.Draw();
            ImGui.PopStyleColor(3);
        }
    }
}
