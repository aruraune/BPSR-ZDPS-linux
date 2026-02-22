namespace BPSR_ZDPS;

using Hexa.NET.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Runtime.InteropServices;
#if WINDOWS
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
#else
using Silk.NET.OpenGL;
using Hexa.NET.GLFW;
#endif

public static unsafe class ImageHelper
{
    public static Dictionary<string, ImTextureRef> LoadedImages = [];
    public static Dictionary<string, ImTextureRef> KeyedImages = [];
    private static Dictionary<ulong, ulong> Textures = [];

#if WINDOWS
    private static D3D11Manager? _manager = null;

    public static void SetDeviceManager(D3D11Manager manager)
    {
        _manager = manager;
    }
#else
    private static OpenGLManager? _manager = null;
    private static GL? _gl = null;

    // Simple GLFW context wrapper for Silk.NET.OpenGL
    private class GLFWGLContext : Silk.NET.Core.Contexts.IGLContext
    {
        private readonly GLFWwindowPtr _window;

        public GLFWGLContext(GLFWwindowPtr window)
        {
            _window = window;
        }

        public nint Handle => (nint)_window.Handle;
        public Silk.NET.Core.Contexts.IGLContextSource? Source => null;

        public bool IsCurrent => Hexa.NET.GLFW.GLFW.GetCurrentContext() == _window;

        public void Dispose() { }

        public nint GetProcAddress(string proc, int? slot = null)
        {
            return (nint)Hexa.NET.GLFW.GLFW.GetProcAddress(proc);
        }

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        {
            addr = GetProcAddress(proc, slot);
            return addr != 0;
        }

        public void MakeCurrent()
        {
            Hexa.NET.GLFW.GLFW.MakeContextCurrent(_window);
        }

        public void SwapBuffers()
        {
            Hexa.NET.GLFW.GLFW.SwapBuffers(_window);
        }

        public void SwapInterval(int interval)
        {
            Hexa.NET.GLFW.GLFW.SwapInterval(interval);
        }

        public void Clear()
        {
            // Not needed for our use case
        }
    }

    public static void SetDeviceManager(OpenGLManager manager)
    {
        _manager = manager;
        // Get OpenGL context from GLFW
        var context = new GLFWGLContext(manager.Window);
        _gl = GL.GetApi(context);
        Serilog.Log.Information("OpenGL texture loading initialized");
    }
#endif

#if WINDOWS
    public static ImTextureRef? LoadTexture(string filePath, string? key = null)
    {
        try
        {
            return LoadTexture(_manager.Device, _manager.DeviceContext, filePath, key);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error encountered during ImageHelper.LoadTexture. Attempting a null return to save the process...");
            return null;
        }
    }

    public static ImTextureRef? LoadTexture(ID3D11Device1* device, ID3D11DeviceContext1* context, string filePath, string? key = null)
    {
        if (LoadedImages.TryGetValue(filePath, out var cachedRef))
            return cachedRef;

        // TODO: Change this so if it finds a local file, it loads it but if not, it search the internal assembly, and lastly a web request
        if (!File.Exists(filePath))
        {
            return null;
        }
        /*else if (System.Reflection.Assembly.GetEntryAssembly().GetManifestResourceNames().Contains(filePath))
        {
            System.Reflection.Assembly.GetEntryAssembly().GetManifestResourceStream(filePath);
        }
        else
        {
            // TODO: Attempt an WebRequest to get the image
        }*/

        if (device == null)
        {
            return null;
        }

        using Image<Rgba32> image = Image.Load<Rgba32>(filePath);
        byte[] pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);

        var texDesc = new Texture2DDesc
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Immutable,
            BindFlags = (uint)BindFlag.ShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };

        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var initData = new SubresourceData
            {
                PSysMem = pinned.AddrOfPinnedObject().ToPointer(),
                SysMemPitch = (uint)(image.Width * 4),
                SysMemSlicePitch = 0
            };

            ID3D11Texture2D* texture = null;
            int hr = ((ID3D11Device*)device)->CreateTexture2D(&texDesc, &initData, &texture);
            Silk.NET.Core.Native.SilkMarshal.ThrowHResult(hr);

            ID3D11ShaderResourceView* srv = null;
            hr = ((ID3D11Device*)device)->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv);
            Silk.NET.Core.Native.SilkMarshal.ThrowHResult(hr);

            Textures.TryAdd((ulong)srv, (ulong)texture);

            var texRef = new ImTextureRef(null, srv);
            LoadedImages.TryAdd(filePath, texRef);

            if (key != null)
            {
                KeyedImages.TryAdd(key, texRef);
            }

            return texRef;
        }
        finally
        {
            pinned.Free();
        }
    }
#else
    public static ImTextureRef? LoadTexture(string filePath, string? key = null)
    {
        try
        {
            if (LoadedImages.TryGetValue(filePath, out var cachedRef))
                return cachedRef;

            if (!File.Exists(filePath))
            {
                return null;
            }

            if (_gl == null)
            {
                Serilog.Log.Error("OpenGL context not initialized in ImageHelper");
                return null;
            }

            using Image<Rgba32> image = Image.Load<Rgba32>(filePath);
            byte[] pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);

            // Generate OpenGL texture
            uint textureId = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, textureId);

            // Upload pixel data
            unsafe
            {
                fixed (byte* ptr = pixels)
                {
                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba,
                        (uint)image.Width,
                        (uint)image.Height,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        ptr
                    );
                }
            }

            // Set texture parameters
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            _gl.BindTexture(TextureTarget.Texture2D, 0);

            // Store texture ID for cleanup
            Textures.TryAdd((ulong)textureId, (ulong)textureId);

            // ImGui expects texture ID as void*
            var texRef = new ImTextureRef(null, (void*)(nuint)textureId);
            LoadedImages.TryAdd(filePath, texRef);

            if (key != null)
            {
                KeyedImages.TryAdd(key, texRef);
            }

            return texRef;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error loading texture on Linux: {FilePath}", filePath);
            return null;
        }
    }
#endif

    public static ImTextureRef? GetTextureByKey(string key)
    {
        if (KeyedImages.TryGetValue(key, out ImTextureRef texRef))
        {
            return texRef;
        }

        return null;
    }

    public static void UnloadAllImages()
    {
#if WINDOWS
        foreach (var texInfo in Textures)
        {
            ((ID3D11Texture2D*)texInfo.Value)->Release();
            ((ID3D11ShaderResourceView*)texInfo.Key)->Release();
        }
#else
        if (_gl != null)
        {
            foreach (var texId in Textures.Keys)
            {
                _gl.DeleteTexture((uint)texId);
            }
        }
#endif
        LoadedImages.Clear();
        KeyedImages.Clear();
        Textures.Clear();
    }
}