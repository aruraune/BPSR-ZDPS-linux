using Hexa.NET.GLFW;
using Hexa.NET.ImGui;
#if WINDOWS
using Hexa.NET.ImGui.Backends.D3D11;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DirectComposition;
using Silk.NET.DXGI;
#else
using Hexa.NET.ImGui.Backends.OpenGL3;
#endif
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace BPSR_ZDPS
{
    public static class RendererImpl
    {
        public struct ViewportRendererData
        {
#if WINDOWS
            public ComPtr<IDXGISwapChain1> SwapChain;
            public unsafe ID3D11RenderTargetView* RTV;
            public unsafe IDCompositionTarget* CompositionTarget;
#endif
            public Vector4 ClearColor;
            public uint SyncInterval;
            public int DesiredRenderFPS;
            public bool LimitFPS;
            public DateTime LastRenderTime;
            public int FrameCount;
            public int CopyToGDIEveryNthFrame;

            public void Init()
            {
                unsafe
                {
#if WINDOWS
                    SwapChain = null;
                    RTV = null;
                    CompositionTarget = null;
#endif
                }
                ClearColor = new Vector4(0, 0, 0, 0);
                SyncInterval = 1;
                DesiredRenderFPS = -1;
                LimitFPS = false;
                LastRenderTime = DateTime.Now;
                FrameCount = 0;
                CopyToGDIEveryNthFrame = 2;
            }
        }

        private unsafe static void* OldRendererCreateWindow;
        public static bool EnableGDIBackBufferCopyCompatibility = false;


        public unsafe static void Init(ImGuiContextPtr context)
        {
            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
            OldRendererCreateWindow = platformIO.PlatformCreateWindow;

            platformIO.PlatformCreateWindow = (delegate* unmanaged<ImGuiViewportPtr, void>)&PlatformCreateWindow;
            platformIO.RendererCreateWindow = (delegate* unmanaged<ImGuiViewportPtr, void>)&OnCreateWindow;
            platformIO.RendererDestroyWindow = (delegate* unmanaged<ImGuiViewportPtr, void>)&OnDestroyWindow;
            platformIO.RendererSetWindowSize = (delegate* unmanaged<ImGuiViewportPtr, Vector2, void>)&OnSetWindowSize;
            platformIO.RendererRenderWindow = (delegate* unmanaged<ImGuiViewportPtr, nint, void>)&RendererRenderWindow;
            platformIO.RendererSwapBuffers = (delegate* unmanaged<ImGuiViewportPtr, nint, void>)&OnSwapBuffers;
        }

        [UnmanagedCallersOnly]
        static unsafe void PlatformCreateWindow(ImGuiViewportPtr viewport)
        {
#if WINDOWS
            GLFW.WindowHint(GLFW.GLFW_CLIENT_API, 0);
            GLFW.WindowHint(GLFW.GLFW_TRANSPARENT_FRAMEBUFFER, 1);
#else
            // Match the main window's OpenGL context version
            GLFW.WindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.WindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 3);
            GLFW.WindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
#endif
            ((delegate* unmanaged<ImGuiViewportPtr, void>)OldRendererCreateWindow)(viewport);
        }

        [UnmanagedCallersOnly]
        private unsafe static void OnCreateWindow(ImGuiViewportPtr viewport)
        {
            var rdata = (ViewportRendererData*)NativeMemory.Alloc((nuint)sizeof(ViewportRendererData));
            rdata->Init();

#if WINDOWS
            SwapChainDesc1 desc = new()
            {
                Width = (uint)viewport.Size.X,
                Height = (uint)viewport.Size.Y,
                Format = Format.FormatB8G8R8A8Unorm,
                BufferCount = 2,
                BufferUsage = DXGI.UsageRenderTargetOutput,
                SampleDesc = new(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                Flags = (uint)(SwapChainFlag.AllowTearing),
                AlphaMode = AlphaMode.Premultiplied
            };

            if (EnableGDIBackBufferCopyCompatibility)
            {
                desc.Flags |= (uint)SwapChainFlag.GdiCompatible;
            }

            SwapChainFullscreenDesc fullscreenDesc = new()
            {
                Windowed = 1,
                RefreshRate = new Rational(0, 1),
                Scaling = ModeScaling.Unspecified,
                ScanlineOrdering = ModeScanlineOrder.Unspecified,
            };

            Program.manager.IDXGIFactory.CreateSwapChainForComposition((IUnknown*)Program.manager.Device.Handle, &desc, (IDXGIOutput*)null, &rdata->SwapChain.Handle);
            Program.manager.DCompositionDesktopDevice.CreateTargetForHwnd((nint)viewport.PlatformHandleRaw, true, &rdata->CompositionTarget);

            IDCompositionVisual2* visual;
            Program.manager.DCompositionDesktopDevice.CreateVisual(&visual);

            visual->SetContent((IUnknown*)rdata->SwapChain.Handle);

            ComPtr<IDCompositionVisual> visual2 = default;
            visual->QueryInterface(out visual2);
            rdata->CompositionTarget->SetRoot(visual2);

            Program.manager.DCompositionDesktopDevice.Commit();

            ID3D11Texture2D* backBuffer;
            Guid guid = ID3D11Texture2D.Guid;
            rdata->SwapChain.GetBuffer(0, &guid, (void**)&backBuffer);

            Program.manager.Device.CreateRenderTargetView(
                (ID3D11Resource*)backBuffer,
                (RenderTargetViewDesc*)null,
                &rdata->RTV);

            backBuffer->Release();

            // Maybe safe to do here
            visual->Release();
            visual2.Dispose();
#endif

            viewport.RendererUserData = rdata;
        }

        [UnmanagedCallersOnly]
        private unsafe static void OnDestroyWindow(ImGuiViewportPtr viewport)
        {
            var rdata = (ViewportRendererData*)viewport.RendererUserData;

            if (rdata == null)
            {
                return;
            }

#if WINDOWS
            rdata->RTV->Release();
            rdata->RTV = null;
            rdata->SwapChain.Dispose();
            rdata->SwapChain = null;
            rdata->CompositionTarget->Release();
            rdata->CompositionTarget = null;
#endif

            NativeMemory.Free(rdata);
            viewport.RendererUserData = null;
        }

        [UnmanagedCallersOnly]
        private unsafe static void OnSetWindowSize(ImGuiViewportPtr viewport, Vector2 size)
        {
#if WINDOWS
            var rdata = (ViewportRendererData*)viewport.RendererUserData;

            Program.manager.DeviceContext.Handle->OMSetRenderTargets(0, null, null);

            if (rdata->RTV != null)
            {
                rdata->RTV->Release();
                rdata->RTV = null;
            }

            ID3D11Texture2D* backBuffer;
            rdata->SwapChain.GetBuffer(
                0,
                SilkMarshal.GuidPtrOf<ID3D11Texture2D>(),
                (void**)&backBuffer
            );

            backBuffer->Release();

            int code = rdata->SwapChain.ResizeBuffers(
                0,
                (uint)size.X,
                (uint)size.Y,
                Format.FormatUnknown,
                (uint)(SwapChainFlag.AllowModeSwitch | SwapChainFlag.AllowTearing)
            );

            rdata->SwapChain.GetBuffer(
                0,
                SilkMarshal.GuidPtrOf<ID3D11Texture2D>(),
                (void**)&backBuffer
            );

            Program.manager.Device.CreateRenderTargetView(
                (ID3D11Resource*)backBuffer,
                (RenderTargetViewDesc*)null,
                &rdata->RTV
            );

            backBuffer->Release();
#endif
        }

        [UnmanagedCallersOnly]
        private static unsafe void RendererRenderWindow(ImGuiViewportPtr viewport, nint v)
        {
            var rdata = (ViewportRendererData*)viewport.RendererUserData;
            var fpsMs = 1000 / rdata->DesiredRenderFPS;

            if (rdata != null &&
                rdata->DesiredRenderFPS == -1 || rdata->LastRenderTime + TimeSpan.FromMilliseconds(fpsMs) < DateTime.Now)
            {
                var start = Stopwatch.GetTimestamp();

#if WINDOWS
                Program.manager.DeviceContext.Handle->OMSetRenderTargets(1, &rdata->RTV, null);
                Program.manager.DeviceContext.Handle->ClearRenderTargetView(rdata->RTV, (float*)&rdata->ClearColor);

                ImGuiImplD3D11.RenderDrawData(viewport.DrawData);
#else
                // Make this viewport's GL context current before rendering
                var glfwWindow = (GLFWwindowPtr)(GLFWwindow*)viewport.PlatformHandle;
                GLFW.MakeContextCurrent(glfwWindow);

                ImGuiImplOpenGL3.RenderDrawData(viewport.DrawData);
#endif

                var end = Stopwatch.GetTimestamp();

                double elapsedMs = (end - start) * 1000.0 / Stopwatch.Frequency;
                rdata->LastRenderTime = DateTime.Now;

                if (rdata->LimitFPS)
                {
                    int sleep = (int)(fpsMs - elapsedMs);
                    if (sleep > 0)
                    {
                        Thread.Sleep(sleep);
                    }
                }
            }
        }

        [UnmanagedCallersOnly]
        private unsafe static void OnSwapBuffers(ImGuiViewportPtr viewport, nint v)
        {
            var rdata = (ViewportRendererData*)viewport.RendererUserData;
            if (rdata != null)
            {
#if WINDOWS
                rdata->SwapChain.Present(rdata->SyncInterval, 0);
#else
                var glfwWindow = (GLFWwindowPtr)(GLFWwindow*)viewport.PlatformHandle;
                GLFW.SwapBuffers(glfwWindow);
#endif
            }
        }
    }
}
