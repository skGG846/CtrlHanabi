using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace CtrlHanabi;

internal sealed class D3DParticleRenderer : IDisposable
{
    private const int CircleSegments = 24;
    private const double TrailEdgeFeatherPixels = 1.25;
    private const int FilledCircleVertexCount = CircleSegments * 3;
    private const int RingStartVertex = FilledCircleVertexCount;
    private const int RingVertexCount = CircleSegments * 6;
    private static readonly bool Direct3D11Enabled =
        !string.Equals(Environment.GetEnvironmentVariable("CTRLHANABI_D3D11"), "0", StringComparison.Ordinal);
    private static readonly bool D3DLogEnabled = RuntimeLogging.IsD3D11LogEnabled();

    private static readonly byte[] PositionSemantic = "POSITION\0"u8.ToArray();
    private static readonly byte[] TexCoordSemantic = "TEXCOORD\0"u8.ToArray();
    private static readonly byte[] CenterSemantic = "CENTER\0"u8.ToArray();
    private static readonly byte[] RadiusSemantic = "RADIUS\0"u8.ToArray();
    private static readonly byte[] ColorSemantic = "COLOR\0"u8.ToArray();
    private static readonly byte[] VertexShaderSource = Encoding.ASCII.GetBytes("""
        struct VS_IN
        {
            float2 unitPosition : POSITION;
            float alphaScale : TEXCOORD;
            float2 center : CENTER;
            float2 radius : RADIUS;
            float4 color : COLOR;
        };
        struct PS_IN { float4 position : SV_POSITION; float4 color : COLOR; };
        PS_IN main(VS_IN input)
        {
            PS_IN output;
            output.position = float4(
                input.center.x + (input.unitPosition.x * input.radius.x),
                input.center.y - (input.unitPosition.y * input.radius.y),
                0.0f,
                1.0f);
            output.color = float4(input.color.rgb, input.color.a * input.alphaScale);
            return output;
        }
        """);
    private static readonly byte[] PixelShaderSource = Encoding.ASCII.GetBytes("""
        struct PS_IN { float4 position : SV_POSITION; float4 color : COLOR; };
        float4 main(PS_IN input) : SV_TARGET
        {
            return input.color;
        }
        """);
    private readonly D3DImage _image = new();
    private D3D11Device? _device11;
    private D3D11DeviceContext? _context11;
    private D3D11Texture2D? _sharedTexture11;
    private D3D11RenderTargetView? _renderTargetView11;
    private D3D11Buffer? _shapeBuffer11;
    private D3D11Buffer? _filledCircleInstanceBuffer11;
    private D3D11Buffer? _softRingInstanceBuffer11;
    private D3D11VertexShader? _vertexShader11;
    private D3D11PixelShader? _pixelShader11;
    private D3D11InputLayout? _inputLayout11;
    private D3D11BlendState? _blendState11;
    private Direct3D9Ex? _direct3D9;
    private Direct3DDevice9Ex? _device9;
    private Direct3DTexture9? _sharedTexture9;
    private Direct3DSurface9? _surface9;
    private int _width;
    private int _height;
    private int _filledCircleInstanceCapacity;
    private int _softRingInstanceCapacity;
    private bool _isDisposed;
    private bool _direct3D11Failed;
    private bool _hasDeviceCreated;

    public D3DParticleRenderer()
    {
        _image.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
    }

    private void OnIsFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_image.IsFrontBufferAvailable)
        {
            _direct3D11Failed = false;
            ReleaseDirect3DResources();
        }
    }

    public ImageSource Image => _image;

    public bool Render(
        FrameworkElement owner,
        List<RenderTrail> trails,
        List<RenderParticle> particles)
    {
        if (_isDisposed)
        {
            return false;
        }

        if (!Direct3D11Enabled || _direct3D11Failed || !_image.IsFrontBufferAvailable)
        {
            return false;
        }

        try
        {
            Log("Render begin");
            var width = Math.Max(1, (int)Math.Ceiling(owner.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(owner.ActualHeight));
            EnsureDevice(owner, width, height);
            if (_context11 is null || _renderTargetView11 is null)
            {
                return false;
            }

            _context11.SetRenderTarget(_renderTargetView11);
            Log("Render target set");
            _context11.SetViewport(_width, _height);
            _context11.ClearRenderTarget(_renderTargetView11);
            DrawInstances(trails, particles);

            _context11.Flush();
            Log("Render flush");

            _image.Lock();
            _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            _image.Unlock();
            Log("Render end");
            return true;
        }
        catch (Exception ex)
        {
            Log("Render failed: " + ex);
            if (!_hasDeviceCreated)
            {
                _direct3D11Failed = true;
            }
            ReleaseDirect3DResources();
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (_context11 is not null && _renderTargetView11 is not null)
            {
                _context11.SetRenderTarget(_renderTargetView11);
                _context11.ClearRenderTarget(_renderTargetView11);
                _context11.Flush();
                _image.Lock();
                _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                _image.Unlock();
            }
        }
        catch
        {
        }
    }

    public void Reset()
    {
        ReleaseDirect3DResources();
    }

    public void Dispose()
    {
        _isDisposed = true;
        _image.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
        ReleaseDirect3DResources();
    }

    private void ReleaseDirect3DResources()
    {
        ReleaseRenderTarget();
        _blendState11?.Dispose();
        _blendState11 = null;
        _inputLayout11?.Dispose();
        _inputLayout11 = null;
        _pixelShader11?.Dispose();
        _pixelShader11 = null;
        _vertexShader11?.Dispose();
        _vertexShader11 = null;
        _softRingInstanceBuffer11?.Dispose();
        _softRingInstanceBuffer11 = null;
        _filledCircleInstanceBuffer11?.Dispose();
        _filledCircleInstanceBuffer11 = null;
        _shapeBuffer11?.Dispose();
        _shapeBuffer11 = null;
        _context11?.Dispose();
        _context11 = null;
        _device11?.Dispose();
        _device11 = null;
        _device9?.Dispose();
        _device9 = null;
        _direct3D9?.Dispose();
        _direct3D9 = null;
        _filledCircleInstanceCapacity = 0;
        _softRingInstanceCapacity = 0;
    }

    private void EnsureDevice(FrameworkElement owner, int width, int height)
    {
        if (_device11 is null || _context11 is null || _device9 is null)
        {
            var hwnd = GetWindowHandle(owner);
            if (hwnd == nint.Zero)
            {
                return;
            }

            (_device11, _context11) = D3D11.CreateDevice();
            _hasDeviceCreated = true;
            Log("D3D11 device created");
            CreatePipeline();
            Log("D3D11 pipeline created");
            _direct3D9 = Direct3D9Ex.Create();
            Log("D3D9Ex created");
            _device9 = _direct3D9.CreateDevice(hwnd);
            Log("D3D9Ex device created");
        }

        if (_sharedTexture11 is null || _surface9 is null || width != _width || height != _height)
        {
            Resize(width, height);
        }
    }

    private unsafe void CreatePipeline()
    {
        if (_device11 is null || _context11 is null)
        {
            return;
        }

        using var vertexShaderBlob = D3DCompiler.Compile(VertexShaderSource, "main", "vs_4_0");
        using var pixelShaderBlob = D3DCompiler.Compile(PixelShaderSource, "main", "ps_4_0");
        _vertexShader11 = _device11.CreateVertexShader(vertexShaderBlob.BufferPointer, vertexShaderBlob.BufferSize);
        _pixelShader11 = _device11.CreatePixelShader(pixelShaderBlob.BufferPointer, pixelShaderBlob.BufferSize);

        fixed (byte* positionSemantic = PositionSemantic)
        fixed (byte* texCoordSemantic = TexCoordSemantic)
        fixed (byte* centerSemantic = CenterSemantic)
        fixed (byte* radiusSemantic = RadiusSemantic)
        fixed (byte* colorSemantic = ColorSemantic)
        {
            var elements = stackalloc InputElementDesc[5];
            elements[0] = new InputElementDesc((nint)positionSemantic, 0, D3D11.FormatR32G32Float, 0, 0, D3D11.InputPerVertexData, 0);
            elements[1] = new InputElementDesc((nint)texCoordSemantic, 0, D3D11.FormatR32Float, 8, 0, D3D11.InputPerVertexData, 0);
            elements[2] = new InputElementDesc((nint)centerSemantic, 0, D3D11.FormatR32G32Float, 0, 1, D3D11.InputPerInstanceData, 1);
            elements[3] = new InputElementDesc((nint)radiusSemantic, 0, D3D11.FormatR32G32Float, 8, 1, D3D11.InputPerInstanceData, 1);
            elements[4] = new InputElementDesc((nint)colorSemantic, 0, D3D11.FormatR32G32B32A32Float, 16, 1, D3D11.InputPerInstanceData, 1);
            _inputLayout11 = _device11.CreateInputLayout((nint)elements, 5, vertexShaderBlob.BufferPointer, vertexShaderBlob.BufferSize);
        }

        CreateShapeBuffer();
        _blendState11 = _device11.CreateBlendState();
        _context11.SetInputLayout(_inputLayout11);
        _context11.SetPrimitiveTopology(D3D11.PrimitiveTopologyTriangleList);
        _context11.SetVertexShader(_vertexShader11);
        _context11.SetPixelShader(_pixelShader11);
        _context11.SetBlendState(_blendState11);
    }

    private void Resize(int width, int height)
    {
        if (_device11 is null || _device9 is null)
        {
            return;
        }

        ReleaseRenderTarget();
        _width = width;
        _height = height;
        _sharedTexture11 = _device11.CreateSharedRenderTargetTexture((uint)_width, (uint)_height);
        Log("D3D11 shared texture created");
        _renderTargetView11 = _device11.CreateRenderTargetView(_sharedTexture11);
        Log("D3D11 render target view created");
        using var dxgiResource = _sharedTexture11.QueryInterface(D3D11.IidDxgiResource);
        var sharedHandle = dxgiResource.GetSharedHandle();
        Log("DXGI shared handle acquired");
        _sharedTexture9 = _device9.CreateSharedTexture((uint)_width, (uint)_height, sharedHandle);
        Log("D3D9 shared texture opened");
        _surface9 = _sharedTexture9.GetSurfaceLevel();
        Log("D3D9 surface acquired");

        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer);
        _image.Unlock();
        Log("D3DImage back buffer set");
    }

    private void ReleaseRenderTarget()
    {
        if (_image.IsFrontBufferAvailable)
        {
            _image.Lock();
            _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, nint.Zero);
            _image.Unlock();
        }

        _surface9?.Dispose();
        _surface9 = null;
        _sharedTexture9?.Dispose();
        _sharedTexture9 = null;
        _renderTargetView11?.Dispose();
        _renderTargetView11 = null;
        _sharedTexture11?.Dispose();
        _sharedTexture11 = null;
        _width = 0;
        _height = 0;
    }

    private unsafe void CreateShapeBuffer()
    {
        if (_device11 is null || _context11 is null)
        {
            return;
        }

        var vertices = new ShapeVertex[FilledCircleVertexCount + RingVertexCount];
        var index = 0;
        var center = new ShapeVertex(0, 0, 1);
        for (var i = 0; i < CircleSegments; i++)
        {
            vertices[index++] = center;
            vertices[index++] = CreateShapeVertex(1, i, 1);
            vertices[index++] = CreateShapeVertex(1, i + 1, 1);
        }

        for (var i = 0; i < CircleSegments; i++)
        {
            var inner0 = CreateShapeVertex(0, i, 1);
            var outer0 = CreateShapeVertex(1, i, 0);
            var inner1 = CreateShapeVertex(0, i + 1, 1);
            var outer1 = CreateShapeVertex(1, i + 1, 0);
            vertices[index++] = inner0;
            vertices[index++] = outer0;
            vertices[index++] = inner1;
            vertices[index++] = inner1;
            vertices[index++] = outer0;
            vertices[index++] = outer1;
        }

        _shapeBuffer11 = _device11.CreateDynamicVertexBuffer((uint)(vertices.Length * sizeof(ShapeVertex)));
        var mapped = _context11.Map(_shapeBuffer11);
        fixed (ShapeVertex* sourcePointer = vertices)
        {
            Buffer.MemoryCopy(sourcePointer, (void*)mapped.DataPointer, mapped.RowPitch, vertices.Length * sizeof(ShapeVertex));
        }

        _context11.Unmap(_shapeBuffer11);
    }

    private static ShapeVertex CreateShapeVertex(double radius, int segmentIndex, float alphaScale)
    {
        var angle = (Math.PI * 2 * segmentIndex) / CircleSegments;
        return new ShapeVertex((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius), alphaScale);
    }

    private unsafe void DrawInstances(List<RenderTrail> trails, List<RenderParticle> particles)
    {
        if (_device11 is null || _context11 is null || _shapeBuffer11 is null)
        {
            return;
        }

        _context11.SetShapeVertexBuffer(_shapeBuffer11, (uint)sizeof(ShapeVertex));
        var maxFilledInstanceCount = trails.Count + (particles.Count * 2);
        var maxRingInstanceCount = trails.Count;
        if (maxFilledInstanceCount > 0)
        {
            EnsureInstanceBuffer(ref _filledCircleInstanceBuffer11, ref _filledCircleInstanceCapacity, maxFilledInstanceCount);
            var filledCount = WriteFilledInstances(_filledCircleInstanceBuffer11, trails, particles);
            if (filledCount > 0)
            {
                _context11.SetInstanceVertexBuffer(_filledCircleInstanceBuffer11!, (uint)sizeof(CircleInstance));
                _context11.DrawInstanced(FilledCircleVertexCount, (uint)filledCount, 0);
            }
        }

        if (maxRingInstanceCount > 0)
        {
            EnsureInstanceBuffer(ref _softRingInstanceBuffer11, ref _softRingInstanceCapacity, maxRingInstanceCount);
            var ringCount = WriteRingInstances(_softRingInstanceBuffer11, trails);
            if (ringCount > 0)
            {
                _context11.SetInstanceVertexBuffer(_softRingInstanceBuffer11!, (uint)sizeof(CircleInstance));
                _context11.DrawInstanced(RingVertexCount, (uint)ringCount, RingStartVertex);
            }
        }
    }

    private void EnsureInstanceBuffer(ref D3D11Buffer? buffer, ref int capacity, int instanceCount)
    {
        if (_device11 is null || instanceCount <= capacity)
        {
            return;
        }

        buffer?.Dispose();
        capacity = Math.Max(instanceCount, capacity == 0 ? 1024 : capacity * 2);
        buffer = _device11.CreateDynamicVertexBuffer((uint)(capacity * Marshal.SizeOf<CircleInstance>()));
    }

    private unsafe int WriteFilledInstances(D3D11Buffer? buffer, List<RenderTrail> trails, List<RenderParticle> particles)
    {
        if (_context11 is null || buffer is null)
        {
            return 0;
        }

        var mapped = _context11.Map(buffer);
        var destination = (CircleInstance*)mapped.DataPointer;
        var count = 0;
        foreach (var trail in CollectionsMarshal.AsSpan(trails))
        {
            var innerRadius = Math.Max(0, (trail.Size / 2) - TrailEdgeFeatherPixels);
            if (innerRadius > 0.25)
            {
                destination[count++] = CreateCircleInstance(trail.X, trail.Y, innerRadius, trail.Color, trail.Opacity);
            }
        }

        foreach (var particle in CollectionsMarshal.AsSpan(particles))
        {
            var glowRadius = particle.GlowSize / 2;
            if (glowRadius > 0)
            {
                destination[count++] = CreateCircleInstance(particle.X, particle.Y, glowRadius, particle.GlowColor, particle.GlowOpacity);
            }

            var coreRadius = particle.CoreSize / 2;
            if (coreRadius > 0)
            {
                destination[count++] = CreateCircleInstance(particle.X, particle.Y, coreRadius, particle.CoreColor, particle.CoreOpacity);
            }
        }

        _context11.Unmap(buffer);
        return count;
    }

    private unsafe int WriteRingInstances(D3D11Buffer? buffer, List<RenderTrail> trails)
    {
        if (_context11 is null || buffer is null)
        {
            return 0;
        }

        var mapped = _context11.Map(buffer);
        var destination = (CircleInstance*)mapped.DataPointer;
        var count = 0;
        foreach (var trail in CollectionsMarshal.AsSpan(trails))
        {
            var radius = trail.Size / 2;
            if (radius > 0)
            {
                destination[count++] = CreateCircleInstance(trail.X, trail.Y, radius, trail.Color, trail.Opacity);
            }
        }

        _context11.Unmap(buffer);
        return count;
    }

    private CircleInstance CreateCircleInstance(double x, double y, double radius, WpfColor color, double opacity)
    {
        var clipX = ((float)x / _width * 2.0f) - 1.0f;
        var clipY = 1.0f - ((float)y / _height * 2.0f);
        var radiusX = (float)radius / _width * 2.0f;
        var radiusY = (float)radius / _height * 2.0f;
        var colorVector = ToColor(color, opacity);
        return new CircleInstance(clipX, clipY, radiusX, radiusY, colorVector.R, colorVector.G, colorVector.B, colorVector.A);
    }

    private static ColorVector ToColor(WpfColor color, double opacity)
    {
        var alpha = color.A / 255.0f * (float)Math.Clamp(opacity, 0, 1);
        return new ColorVector(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, alpha);
    }

    private static nint GetWindowHandle(FrameworkElement owner)
    {
        var window = Window.GetWindow(owner);
        return window is null ? nint.Zero : new WindowInteropHelper(window).Handle;
    }

    private static void Log(string message)
    {
        if (!D3DLogEnabled)
        {
            return;
        }

        RuntimeLogging.AppendD3D11Log(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShapeVertex(float x, float y, float alphaScale)
    {
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float AlphaScale = alphaScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CircleInstance(float x, float y, float radiusX, float radiusY, float r, float g, float b, float a)
    {
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float RadiusX = radiusX;
        public readonly float RadiusY = radiusY;
        public readonly float R = r;
        public readonly float G = g;
        public readonly float B = b;
        public readonly float A = a;
    }

    private readonly record struct ColorVector(float R, float G, float B, float A);

    private static unsafe class D3D11
    {
        public static readonly Guid IidDxgiResource = new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");
        public const uint SdkVersion = 7;
        public const uint DriverTypeHardware = 1;
        public const uint DriverTypeWarp = 5;
        public const uint CreateDeviceBgraSupport = 0x20;
        public const uint FormatR32G32Float = 16;
        public const uint FormatR32G32B32A32Float = 2;
        public const uint FormatR32Float = 41;
        public const uint FormatB8G8R8A8Unorm = 87;
        public const uint BindVertexBuffer = 0x1;
        public const uint BindShaderResource = 0x8;
        public const uint BindRenderTarget = 0x20;
        public const uint UsageDefault = 0;
        public const uint UsageDynamic = 2;
        public const uint CpuAccessWrite = 0x10000;
        public const uint ResourceMiscShared = 0x2;
        public const uint MapWriteDiscard = 4;
        public const uint InputPerVertexData = 0;
        public const uint InputPerInstanceData = 1;
        public const uint PrimitiveTopologyTriangleList = 4;
        public const uint BlendOne = 2;
        public const uint BlendOpAdd = 1;
        public const uint BlendSourceAlpha = 5;
        public const uint BlendInverseSourceAlpha = 6;
        public const byte ColorWriteEnableAll = 0x0F;

        [DllImport("d3d11.dll", ExactSpelling = true)]
        public static extern int D3D11CreateDevice(
            nint adapter,
            uint driverType,
            nint software,
            uint flags,
            nint featureLevels,
            uint featureLevelsCount,
            uint sdkVersion,
            out nint device,
            out uint featureLevel,
            out nint immediateContext);

        public static (D3D11Device Device, D3D11DeviceContext Context) CreateDevice()
        {
            var result = D3D11CreateDevice(
                nint.Zero,
                DriverTypeHardware,
                nint.Zero,
                CreateDeviceBgraSupport,
                nint.Zero,
                0,
                SdkVersion,
                out var devicePointer,
                out _,
                out var contextPointer);
            if (result < 0)
            {
                result = D3D11CreateDevice(
                    nint.Zero,
                    DriverTypeWarp,
                    nint.Zero,
                    CreateDeviceBgraSupport,
                    nint.Zero,
                    0,
                    SdkVersion,
                    out devicePointer,
                    out _,
                    out contextPointer);
            }

            ThrowIfFailed(result);
            return (new D3D11Device(devicePointer), new D3D11DeviceContext(contextPointer));
        }

        public static void ThrowIfFailed(int result)
        {
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
    }

    private static unsafe class D3DCompiler
    {
        [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
        private static extern int D3DCompile(
            byte[] sourceData,
            nuint sourceDataSize,
            nint sourceName,
            nint defines,
            nint include,
            byte[] entryPoint,
            byte[] target,
            uint flags1,
            uint flags2,
            out nint code,
            out nint errorMessages);

        public static D3DBlob Compile(byte[] source, string entryPoint, string target)
        {
            var entryBytes = Encoding.ASCII.GetBytes(entryPoint + '\0');
            var targetBytes = Encoding.ASCII.GetBytes(target + '\0');
            var result = D3DCompile(
                source,
                (nuint)source.Length,
                nint.Zero,
                nint.Zero,
                nint.Zero,
                entryBytes,
                targetBytes,
                0,
                0,
                out var code,
                out var errors);
            using var errorBlob = errors == nint.Zero ? null : new D3DBlob(errors);
            if (result < 0)
            {
                throw new InvalidOperationException(errorBlob is null ? "D3DCompile failed." : errorBlob.ReadAnsiString());
            }

            return new D3DBlob(code);
        }
    }

    private static unsafe class D3D9
    {
        public const uint SdkVersion = 32;
        public const uint FormatA8R8G8B8 = 21;
        public const uint SwapEffectDiscard = 1;
        public const uint PresentIntervalImmediate = 0x80000000;
        public const uint CreateFpuPreserve = 0x00000002;
        public const uint CreateMultithreaded = 0x00000004;
        public const uint CreateHardwareVertexProcessing = 0x00000040;
        public const uint DeviceTypeHardware = 1;
        public const uint UsageRenderTarget = 0x00000001;
        public const uint PoolDefault = 0;

        [DllImport("d3d9.dll", ExactSpelling = true)]
        public static extern int Direct3DCreate9Ex(uint sdkVersion, out nint direct3D);

        public static void ThrowIfFailed(int result)
        {
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct InputElementDesc(nint semanticName, uint semanticIndex, uint format, uint alignedByteOffset, uint inputSlot, uint inputSlotClass, uint instanceDataStepRate)
    {
        public readonly nint SemanticName = semanticName;
        public readonly uint SemanticIndex = semanticIndex;
        public readonly uint Format = format;
        public readonly uint InputSlot = inputSlot;
        public readonly uint AlignedByteOffset = alignedByteOffset;
        public readonly uint InputSlotClass = inputSlotClass;
        public readonly uint InstanceDataStepRate = instanceDataStepRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint DataPointer;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Viewport(float width, float height)
    {
        public readonly float TopLeftX = 0;
        public readonly float TopLeftY = 0;
        public readonly float Width = width;
        public readonly float Height = height;
        public readonly float MinDepth = 0;
        public readonly float MaxDepth = 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendDesc
    {
        public int AlphaToCoverageEnable;
        public int IndependentBlendEnable;
        public RenderTargetBlendDesc RenderTarget0;
        public RenderTargetBlendDesc RenderTarget1;
        public RenderTargetBlendDesc RenderTarget2;
        public RenderTargetBlendDesc RenderTarget3;
        public RenderTargetBlendDesc RenderTarget4;
        public RenderTargetBlendDesc RenderTarget5;
        public RenderTargetBlendDesc RenderTarget6;
        public RenderTargetBlendDesc RenderTarget7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderTargetBlendDesc
    {
        public int BlendEnable;
        public uint SrcBlend;
        public uint DestBlend;
        public uint BlendOp;
        public uint SrcBlendAlpha;
        public uint DestBlendAlpha;
        public uint BlendOpAlpha;
        public byte RenderTargetWriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PresentParameters
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint BackBufferFormat;
        public uint BackBufferCount;
        public uint MultiSampleType;
        public uint MultiSampleQuality;
        public uint SwapEffect;
        public nint DeviceWindowHandle;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreenRefreshRateInHz;
        public uint PresentationInterval;
    }

    private unsafe class ComPtr(nint nativePointer) : IDisposable
    {
        private bool _isDisposed;

        public nint NativePointer { get; private set; } = nativePointer;

        public ComPtr QueryInterface(Guid iid)
        {
            nint queried = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
            D3D11.ThrowIfFailed(queryInterface(NativePointer, &iid, &queried));
            return new ComPtr(queried);
        }

        public nint GetSharedHandle()
        {
            nint sharedHandle = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var getSharedHandle = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[8];
            D3D11.ThrowIfFailed(getSharedHandle(NativePointer, &sharedHandle));
            return sharedHandle;
        }

        public virtual void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (NativePointer != nint.Zero)
            {
                var vtable = *(nint**)NativePointer;
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
                release(NativePointer);
                NativePointer = nint.Zero;
            }
        }
    }

    private sealed unsafe class D3DBlob(nint nativePointer) : ComPtr(nativePointer)
    {
        public nint BufferPointer
        {
            get
            {
                var vtable = *(nint**)NativePointer;
                var getBufferPointer = (delegate* unmanaged[Stdcall]<nint, nint>)vtable[3];
                return getBufferPointer(NativePointer);
            }
        }

        public nuint BufferSize
        {
            get
            {
                var vtable = *(nint**)NativePointer;
                var getBufferSize = (delegate* unmanaged[Stdcall]<nint, nuint>)vtable[4];
                return getBufferSize(NativePointer);
            }
        }

        public string ReadAnsiString() => Marshal.PtrToStringAnsi(BufferPointer, (int)BufferSize) ?? string.Empty;
    }

    private sealed unsafe class D3D11Device(nint nativePointer) : ComPtr(nativePointer)
    {
        public D3D11Buffer CreateDynamicVertexBuffer(uint byteWidth)
        {
            var desc = new BufferDesc
            {
                ByteWidth = byteWidth,
                Usage = D3D11.UsageDynamic,
                BindFlags = D3D11.BindVertexBuffer,
                CpuAccessFlags = D3D11.CpuAccessWrite
            };
            nint bufferPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createBuffer = (delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)vtable[3];
            D3D11.ThrowIfFailed(createBuffer(NativePointer, &desc, nint.Zero, &bufferPointer));
            return new D3D11Buffer(bufferPointer);
        }

        public D3D11Texture2D CreateSharedRenderTargetTexture(uint width, uint height)
        {
            var desc = new Texture2DDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = D3D11.FormatB8G8R8A8Unorm,
                SampleCount = 1,
                Usage = D3D11.UsageDefault,
                BindFlags = D3D11.BindRenderTarget | D3D11.BindShaderResource,
                MiscFlags = D3D11.ResourceMiscShared
            };
            nint texturePointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createTexture = (delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)vtable[5];
            D3D11.ThrowIfFailed(createTexture(NativePointer, &desc, nint.Zero, &texturePointer));
            return new D3D11Texture2D(texturePointer);
        }

        public D3D11RenderTargetView CreateRenderTargetView(D3D11Texture2D texture)
        {
            nint viewPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createRenderTargetView = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)vtable[9];
            D3D11.ThrowIfFailed(createRenderTargetView(NativePointer, texture.NativePointer, nint.Zero, &viewPointer));
            return new D3D11RenderTargetView(viewPointer);
        }

        public D3D11InputLayout CreateInputLayout(nint elements, uint elementCount, nint shaderBytecode, nuint bytecodeLength)
        {
            nint layoutPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createInputLayout = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nuint, nint*, int>)vtable[11];
            D3D11.ThrowIfFailed(createInputLayout(NativePointer, elements, elementCount, shaderBytecode, bytecodeLength, &layoutPointer));
            return new D3D11InputLayout(layoutPointer);
        }

        public D3D11VertexShader CreateVertexShader(nint shaderBytecode, nuint bytecodeLength)
        {
            nint shaderPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createVertexShader = (delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)vtable[12];
            D3D11.ThrowIfFailed(createVertexShader(NativePointer, shaderBytecode, bytecodeLength, nint.Zero, &shaderPointer));
            return new D3D11VertexShader(shaderPointer);
        }

        public D3D11PixelShader CreatePixelShader(nint shaderBytecode, nuint bytecodeLength)
        {
            nint shaderPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createPixelShader = (delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)vtable[15];
            D3D11.ThrowIfFailed(createPixelShader(NativePointer, shaderBytecode, bytecodeLength, nint.Zero, &shaderPointer));
            return new D3D11PixelShader(shaderPointer);
        }

        public D3D11BlendState CreateBlendState()
        {
            var desc = new BlendDesc
            {
                RenderTarget0 = new RenderTargetBlendDesc
                {
                    BlendEnable = 1,
                    SrcBlend = D3D11.BlendSourceAlpha,
                    DestBlend = D3D11.BlendInverseSourceAlpha,
                    BlendOp = D3D11.BlendOpAdd,
                    SrcBlendAlpha = D3D11.BlendOne,
                    DestBlendAlpha = D3D11.BlendInverseSourceAlpha,
                    BlendOpAlpha = D3D11.BlendOpAdd,
                    RenderTargetWriteMask = D3D11.ColorWriteEnableAll
                }
            };
            nint blendPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createBlendState = (delegate* unmanaged[Stdcall]<nint, BlendDesc*, nint*, int>)vtable[20];
            D3D11.ThrowIfFailed(createBlendState(NativePointer, &desc, &blendPointer));
            return new D3D11BlendState(blendPointer);
        }
    }

    private sealed unsafe class D3D11DeviceContext(nint nativePointer) : ComPtr(nativePointer)
    {
        public MappedSubresource Map(D3D11Buffer buffer)
        {
            MappedSubresource mapped = default;
            var vtable = *(nint**)NativePointer;
            var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)vtable[14];
            D3D11.ThrowIfFailed(map(NativePointer, buffer.NativePointer, 0, D3D11.MapWriteDiscard, 0, &mapped));
            return mapped;
        }

        public void Unmap(D3D11Buffer buffer)
        {
            var vtable = *(nint**)NativePointer;
            var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)vtable[15];
            unmap(NativePointer, buffer.NativePointer, 0);
        }

        public void SetInputLayout(D3D11InputLayout layout)
        {
            var vtable = *(nint**)NativePointer;
            var iaSetInputLayout = (delegate* unmanaged[Stdcall]<nint, nint, void>)vtable[17];
            iaSetInputLayout(NativePointer, layout.NativePointer);
        }

        public void SetShapeVertexBuffer(D3D11Buffer buffer, uint stride)
            => SetVertexBuffer(0, buffer, stride);

        public void SetInstanceVertexBuffer(D3D11Buffer buffer, uint stride)
            => SetVertexBuffer(1, buffer, stride);

        private void SetVertexBuffer(uint slot, D3D11Buffer buffer, uint stride)
        {
            var bufferPointer = buffer.NativePointer;
            uint offset = 0;
            var vtable = *(nint**)NativePointer;
            var iaSetVertexBuffers = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, uint*, void>)vtable[18];
            iaSetVertexBuffers(NativePointer, slot, 1, &bufferPointer, &stride, &offset);
        }

        public void SetPrimitiveTopology(uint topology)
        {
            var vtable = *(nint**)NativePointer;
            var iaSetPrimitiveTopology = (delegate* unmanaged[Stdcall]<nint, uint, void>)vtable[24];
            iaSetPrimitiveTopology(NativePointer, topology);
        }

        public void SetRenderTarget(D3D11RenderTargetView renderTargetView)
        {
            var viewPointer = renderTargetView.NativePointer;
            var vtable = *(nint**)NativePointer;
            var omSetRenderTargets = (delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)vtable[33];
            omSetRenderTargets(NativePointer, 1, &viewPointer, nint.Zero);
        }

        public void SetBlendState(D3D11BlendState blendState)
        {
            var blendFactor = stackalloc float[4];
            var vtable = *(nint**)NativePointer;
            var omSetBlendState = (delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)vtable[35];
            omSetBlendState(NativePointer, blendState.NativePointer, blendFactor, uint.MaxValue);
        }

        public void SetViewport(int width, int height)
        {
            var viewport = new Viewport(width, height);
            var vtable = *(nint**)NativePointer;
            var rsSetViewports = (delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)vtable[44];
            rsSetViewports(NativePointer, 1, &viewport);
        }

        public void ClearRenderTarget(D3D11RenderTargetView renderTargetView)
        {
            var clearColor = stackalloc float[4];
            var vtable = *(nint**)NativePointer;
            var clearRenderTargetView = (delegate* unmanaged[Stdcall]<nint, nint, float*, void>)vtable[50];
            clearRenderTargetView(NativePointer, renderTargetView.NativePointer, clearColor);
        }

        public void SetVertexShader(D3D11VertexShader shader)
        {
            var vtable = *(nint**)NativePointer;
            var vsSetShader = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)vtable[11];
            vsSetShader(NativePointer, shader.NativePointer, nint.Zero, 0);
        }

        public void SetPixelShader(D3D11PixelShader shader)
        {
            var vtable = *(nint**)NativePointer;
            var psSetShader = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)vtable[9];
            psSetShader(NativePointer, shader.NativePointer, nint.Zero, 0);
        }

        public void Draw(uint vertexCount)
        {
            var vtable = *(nint**)NativePointer;
            var draw = (delegate* unmanaged[Stdcall]<nint, uint, uint, void>)vtable[13];
            draw(NativePointer, vertexCount, 0);
        }

        public void DrawInstanced(int vertexCount, uint instanceCount, int startVertex)
        {
            var vtable = *(nint**)NativePointer;
            var drawInstanced = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)vtable[21];
            drawInstanced(NativePointer, (uint)vertexCount, instanceCount, (uint)startVertex, 0);
        }

        public void Flush()
        {
            var vtable = *(nint**)NativePointer;
            var flush = (delegate* unmanaged[Stdcall]<nint, void>)vtable[111];
            flush(NativePointer);
        }
    }

    private sealed class D3D11Buffer(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11Texture2D(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11RenderTargetView(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11VertexShader(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11PixelShader(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11InputLayout(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11BlendState(nint nativePointer) : ComPtr(nativePointer);

    private sealed unsafe class Direct3D9Ex(nint nativePointer) : ComPtr(nativePointer)
    {
        public static Direct3D9Ex Create()
        {
            D3D9.ThrowIfFailed(D3D9.Direct3DCreate9Ex(D3D9.SdkVersion, out var pointer));
            return new Direct3D9Ex(pointer);
        }

        public Direct3DDevice9Ex CreateDevice(nint hwnd)
        {
            var presentParameters = new PresentParameters
            {
                Windowed = 1,
                SwapEffect = D3D9.SwapEffectDiscard,
                DeviceWindowHandle = hwnd,
                PresentationInterval = D3D9.PresentIntervalImmediate,
                BackBufferFormat = D3D9.FormatA8R8G8B8,
                BackBufferWidth = 1,
                BackBufferHeight = 1
            };
            const uint createFlags = D3D9.CreateHardwareVertexProcessing | D3D9.CreateMultithreaded | D3D9.CreateFpuPreserve;
            nint devicePointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createDeviceEx = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, PresentParameters*, nint, nint*, int>)vtable[20];
            D3D9.ThrowIfFailed(createDeviceEx(NativePointer, 0, D3D9.DeviceTypeHardware, hwnd, createFlags, &presentParameters, nint.Zero, &devicePointer));
            return new Direct3DDevice9Ex(devicePointer);
        }
    }

    private sealed unsafe class Direct3DDevice9Ex(nint nativePointer) : ComPtr(nativePointer)
    {
        public Direct3DTexture9 CreateSharedTexture(uint width, uint height, nint sharedHandle)
        {
            nint texturePointer = nint.Zero;
            var handle = sharedHandle;
            var vtable = *(nint**)NativePointer;
            var createTexture = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, uint, nint*, nint*, int>)vtable[23];
            var result = createTexture(
                NativePointer,
                width,
                height,
                1,
                D3D9.UsageRenderTarget,
                D3D9.FormatA8R8G8B8,
                D3D9.PoolDefault,
                &texturePointer,
                &handle);
            if (result < 0)
            {
                if (D3DLogEnabled)
                {
                    Log($"D3D9 shared texture open failed: 0x{result:X8}, size={width}x{height}, handle=0x{sharedHandle:X}");
                }
            }

            D3D9.ThrowIfFailed(result);
            return new Direct3DTexture9(texturePointer);
        }
    }

    private sealed unsafe class Direct3DTexture9(nint nativePointer) : ComPtr(nativePointer)
    {
        public Direct3DSurface9 GetSurfaceLevel()
        {
            nint surfacePointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var getSurfaceLevel = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtable[18];
            D3D9.ThrowIfFailed(getSurfaceLevel(NativePointer, 0, &surfacePointer));
            return new Direct3DSurface9(surfacePointer);
        }
    }

    private sealed unsafe class Direct3DSurface9(nint nativePointer) : ComPtr(nativePointer);
}
