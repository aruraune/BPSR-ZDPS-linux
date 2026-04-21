using Hexa.NET.GLFW;
using Serilog;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.InteropServices;

namespace BPSR_ZDPS
{
    public unsafe class OpenGLManager : IDisposable
    {
        private GLFWwindowPtr window;
        public GLFWwindowPtr Window => window;
        public int Width { get; private set; }
        public int Height { get; private set; }
        private GL gl;

        public OpenGLManager(GLFWwindowPtr window, bool debug)
        {
            this.window = window;
            
            int width = 0;
            int height = 0;
            GLFW.GetWindowSize(window, &width, &height);
            
            Width = width;
            Height = height;

            GLFW.MakeContextCurrent(window);

            gl = GL.GetApi(proc => (nint)GLFW.GetProcAddress(proc));

            // On Linux, always enable vsync to cap framerate at monitor refresh rate
            // On Windows, disable vsync for lower latency in overlay mode
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                GLFW.SwapInterval(1); // Enable vsync (caps at monitor refresh rate, e.g., 120Hz)
                Log.Information("Vsync enabled (Linux) - framerate capped at monitor refresh rate");
            }
            else
            {
                GLFW.SwapInterval(0); // Disable vsync for lower latency
            }

            Log.Information("OpenGL context created (Width: {Width}, Height: {Height})", Width, Height);
        }

        public void Present(uint sync, uint flags)
        {
            // On Linux, always use vsync (ignore sync parameter)
            // SwapInterval is already set to 1 in constructor
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                GLFW.SwapInterval(1); // Ensure vsync stays enabled
            }
            GLFW.SwapBuffers(window);
        }

        public void Clear(Vector4 color)
        {
            gl.ClearColor(color.X, color.Y, color.Z, color.W);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        public void SetTarget()
        {
            // OpenGL target is handled by ImGui backend
        }

        public void UnTarget()
        {
            // OpenGL untarget is handled by ImGui backend
        }

        public void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            Log.Debug("OpenGL viewport resized to {Width}x{Height}", width, height);
        }

        public void Dispose()
        {
            GLFW.MakeContextCurrent(GLFWwindowPtr.Null);
            GC.SuppressFinalize(this);
        }
    }
}
