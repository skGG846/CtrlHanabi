using System.Runtime.InteropServices;
using System.Text;

namespace CtrlHanabi;

public partial class FireworkOverlayWindow
{
    private sealed unsafe class GpuParticlePhysics : IDisposable
    {
        private const int ThreadGroupSize = 64;
        private static readonly bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("CTRLHANABI_GPU_PHYSICS"), "1", StringComparison.Ordinal);
        private static readonly byte[] ComputeShaderSource = Encoding.ASCII.GetBytes("""
            struct Particle
            {
                float x;
                float y;
                float z;
                float prevX;
                float prevY;
                float prevZ;
                float vx;
                float vy;
                float vz;
                float life;
                float initialLife;
                float decay;
                float drag;
            };

            cbuffer Params : register(b0)
            {
                uint count;
                float dt;
                float maxDepth;
                float pad;
            };

            RWStructuredBuffer<Particle> particles : register(u0);

            [numthreads(64, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                uint i = id.x;
                if (i >= count)
                {
                    return;
                }

                Particle p = particles[i];
                float age = 1.0f - (p.life / p.initialLife);
                p.prevX = p.x;
                p.prevY = p.y;
                p.prevZ = p.z;
                p.vy += (82.0f + age * 20.0f) * dt;
                p.vx *= p.drag;
                p.vy *= p.drag;
                p.vz *= p.drag;
                p.x += p.vx * dt;
                p.y += p.vy * dt;
                p.z = clamp(p.z + p.vz * dt, -maxDepth, maxDepth);
                p.life -= dt * p.decay;
                particles[i] = p;
            }
            """);

        private readonly List<GpuParticle> _uploadParticles = [];
        private D3D11Device? _device;
        private D3D11DeviceContext? _context;
        private D3D11ComputeShader? _computeShader;
        private D3D11Buffer? _particleBuffer;
        private D3D11Buffer? _uploadBuffer;
        private D3D11Buffer? _stagingBuffer;
        private D3D11Buffer? _paramsBuffer;
        private D3D11UnorderedAccessView? _particleUav;
        private int _capacity;
        private int _gpuParticleCount;
        private int _pendingReadbackCount;
        private bool _failed;

        public bool IsEnabled => Enabled && !_failed;

        public int TryApplyPending(List<Particle> particles)
        {
            if (!Enabled || _failed || _pendingReadbackCount == 0)
            {
                return 0;
            }

            try
            {
                EnsureDevice();
                if (_device is null || _context is null || _computeShader is null)
                {
                    return 0;
                }

                if (_stagingBuffer is null || particles.Count == 0)
                {
                    _pendingReadbackCount = 0;
                    return 0;
                }

                var updatedCount = Math.Min(_pendingReadbackCount, particles.Count);
                if (!_context.TryMapRead(_stagingBuffer, out var mapped))
                {
                    _pendingReadbackCount = 0;
                    _gpuParticleCount = 0;
                    return 0;
                }

                try
                {
                    var mappedParticles = new ReadOnlySpan<GpuParticle>((void*)mapped.DataPointer, updatedCount);
                    for (var i = 0; i < updatedCount; i++)
                    {
                        var gp = mappedParticles[i];
                        var p = particles[i];
                        p.X = gp.X;
                        p.Y = gp.Y;
                        p.Z = gp.Z;
                        p.PrevX = gp.PrevX;
                        p.PrevY = gp.PrevY;
                        p.PrevZ = gp.PrevZ;
                        p.Vx = gp.Vx;
                        p.Vy = gp.Vy;
                        p.Vz = gp.Vz;
                        p.Life = gp.Life;
                        particles[i] = p;
                    }
                }
                finally
                {
                    _context.Unmap(_stagingBuffer);
                }

                _pendingReadbackCount = 0;
                _gpuParticleCount = updatedCount;
                return updatedCount;
            }
            catch
            {
                _failed = true;
                _pendingReadbackCount = 0;
                Release();
                return 0;
            }
        }

        public void ScheduleUpdate(List<Particle> particles, double dt, double maxDepth, bool allowReuseInputBuffer)
        {
            if (!Enabled || _failed)
            {
                return;
            }

            if (particles.Count == 0)
            {
                _pendingReadbackCount = 0;
                _gpuParticleCount = 0;
                return;
            }

            try
            {
                if (_pendingReadbackCount > 0)
                {
                    return;
                }

                EnsureDevice();
                if (_device is null || _context is null || _computeShader is null)
                {
                    return;
                }

                EnsureBuffers(particles.Count);
                if (_particleBuffer is null || _uploadBuffer is null || _stagingBuffer is null || _paramsBuffer is null || _particleUav is null)
                {
                    return;
                }

                var reuseInputBuffer = allowReuseInputBuffer && _gpuParticleCount == particles.Count;
                if (!reuseInputBuffer)
                {
                    _uploadParticles.Clear();
                    _uploadParticles.EnsureCapacity(particles.Count);
                    foreach (var p in particles)
                    {
                        _uploadParticles.Add(new GpuParticle(
                            (float)p.X,
                            (float)p.Y,
                            (float)p.Z,
                            (float)p.PrevX,
                            (float)p.PrevY,
                            (float)p.PrevZ,
                            (float)p.Vx,
                            (float)p.Vy,
                            (float)p.Vz,
                            (float)p.Life,
                            (float)p.InitialLife,
                            (float)p.Decay,
                            (float)p.Drag));
                    }
                    _context.WriteBuffer(_uploadBuffer, CollectionsMarshal.AsSpan(_uploadParticles));
                    _context.CopyResource(_particleBuffer, _uploadBuffer);
                }

                var parameters = new ComputeParams((uint)particles.Count, (float)dt, (float)maxDepth, 0);
                _context.UpdateValue(_paramsBuffer, parameters);
                _context.SetComputeShader(_computeShader);
                _context.SetComputeConstantBuffer(_paramsBuffer);
                _context.SetComputeUnorderedAccessView(_particleUav);
                _context.Dispatch((uint)((particles.Count + ThreadGroupSize - 1) / ThreadGroupSize));
                _context.UnsetComputeUnorderedAccessView();
                _context.CopyResource(_stagingBuffer, _particleBuffer);
                _gpuParticleCount = particles.Count;
                _pendingReadbackCount = particles.Count;
            }
            catch
            {
                _failed = true;
                _pendingReadbackCount = 0;
                Release();
            }
        }

        public void Reset()
        {
            _pendingReadbackCount = 0;
            _gpuParticleCount = 0;
            _uploadParticles.Clear();
        }

        public void Dispose() => Release();

        private void EnsureDevice()
        {
            if (_device is not null && _context is not null && _computeShader is not null)
            {
                return;
            }

            (_device, _context) = D3D11.CreateDevice();
            using var shaderBlob = D3DCompiler.Compile(ComputeShaderSource, "main", "cs_5_0");
            _computeShader = _device.CreateComputeShader(shaderBlob.BufferPointer, shaderBlob.BufferSize);
        }

        private void EnsureBuffers(int count)
        {
            if (_device is null || count <= _capacity)
            {
                return;
            }

            _context?.UnsetComputeUnorderedAccessView();
            _particleUav?.Dispose();
            _particleBuffer?.Dispose();
            _uploadBuffer?.Dispose();
            _stagingBuffer?.Dispose();
            _paramsBuffer?.Dispose();
            _capacity = Math.Max(count, _capacity == 0 ? 1024 : _capacity * 2);
            var byteWidth = (uint)(_capacity * Marshal.SizeOf<GpuParticle>());
            _particleBuffer = _device.CreateStructuredBuffer(byteWidth, (uint)Marshal.SizeOf<GpuParticle>());
            _particleUav = _device.CreateUnorderedAccessView(_particleBuffer);
            _uploadBuffer = _device.CreateUploadBuffer(byteWidth, (uint)Marshal.SizeOf<GpuParticle>());
            _stagingBuffer = _device.CreateStagingBuffer(byteWidth, (uint)Marshal.SizeOf<GpuParticle>());
            _paramsBuffer = _device.CreateConstantBuffer((uint)Marshal.SizeOf<ComputeParams>());
        }

        private void Release()
        {
            _context?.UnsetComputeUnorderedAccessView();
            _particleUav?.Dispose();
            _particleUav = null;
            _paramsBuffer?.Dispose();
            _paramsBuffer = null;
            _stagingBuffer?.Dispose();
            _stagingBuffer = null;
            _uploadBuffer?.Dispose();
            _uploadBuffer = null;
            _particleBuffer?.Dispose();
            _particleBuffer = null;
            _computeShader?.Dispose();
            _computeShader = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _capacity = 0;
            _gpuParticleCount = 0;
            _pendingReadbackCount = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct GpuParticle(
            float x,
            float y,
            float z,
            float prevX,
            float prevY,
            float prevZ,
            float vx,
            float vy,
            float vz,
            float life,
            float initialLife,
            float decay,
            float drag)
        {
            public readonly float X = x;
            public readonly float Y = y;
            public readonly float Z = z;
            public readonly float PrevX = prevX;
            public readonly float PrevY = prevY;
            public readonly float PrevZ = prevZ;
            public readonly float Vx = vx;
            public readonly float Vy = vy;
            public readonly float Vz = vz;
            public readonly float Life = life;
            public readonly float InitialLife = initialLife;
            public readonly float Decay = decay;
            public readonly float Drag = drag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct ComputeParams(uint count, float dt, float maxDepth, float pad)
        {
            public readonly uint Count = count;
            public readonly float Dt = dt;
            public readonly float MaxDepth = maxDepth;
            public readonly float Pad = pad;
        }
    }

    private static unsafe class D3D11
    {
        public const uint SdkVersion = 7;
        public const uint DriverTypeHardware = 1;
        public const uint DriverTypeWarp = 5;
        public const uint CreateDeviceBgraSupport = 0x20;
        public const uint BindConstantBuffer = 0x4;
        public const uint BindUnorderedAccess = 0x80;
        public const uint UsageDefault = 0;
        public const uint UsageStaging = 3;
        public const uint CpuAccessWrite = 0x10000;
        public const uint CpuAccessRead = 0x20000;
        public const uint ResourceMiscBufferStructured = 0x40;
        public const uint MapRead = 1;
        public const uint MapWrite = 2;
        public const uint MapFlagDoNotWait = 0x100000;
        public const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);

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
            var result = D3D11CreateDevice(nint.Zero, DriverTypeHardware, nint.Zero, CreateDeviceBgraSupport, nint.Zero, 0, SdkVersion, out var devicePointer, out _, out var contextPointer);
            if (result < 0)
            {
                result = D3D11CreateDevice(nint.Zero, DriverTypeWarp, nint.Zero, CreateDeviceBgraSupport, nint.Zero, 0, SdkVersion, out devicePointer, out _, out contextPointer);
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
            D3D11.ThrowIfFailed(D3DCompile(source, (nuint)source.Length, nint.Zero, nint.Zero, nint.Zero, entryBytes, targetBytes, 0, 0, out var code, out var errors));
            if (errors != nint.Zero)
            {
                new D3DBlob(errors).Dispose();
            }

            return new D3DBlob(code);
        }
    }

    private unsafe class ComPtr(nint nativePointer) : IDisposable
    {
        private bool _isDisposed;
        public nint NativePointer { get; private set; } = nativePointer;

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
    }

    private sealed unsafe class D3D11Device(nint nativePointer) : ComPtr(nativePointer)
    {
        public D3D11Buffer CreateStructuredBuffer(uint byteWidth, uint stride)
            => CreateBuffer(new BufferDesc { ByteWidth = byteWidth, Usage = D3D11.UsageDefault, BindFlags = D3D11.BindUnorderedAccess, MiscFlags = D3D11.ResourceMiscBufferStructured, StructureByteStride = stride });

        public D3D11Buffer CreateStagingBuffer(uint byteWidth, uint stride)
            => CreateBuffer(new BufferDesc { ByteWidth = byteWidth, Usage = D3D11.UsageStaging, CpuAccessFlags = D3D11.CpuAccessRead, MiscFlags = D3D11.ResourceMiscBufferStructured, StructureByteStride = stride });

        public D3D11Buffer CreateUploadBuffer(uint byteWidth, uint stride)
            => CreateBuffer(new BufferDesc { ByteWidth = byteWidth, Usage = D3D11.UsageStaging, CpuAccessFlags = D3D11.CpuAccessWrite, MiscFlags = D3D11.ResourceMiscBufferStructured, StructureByteStride = stride });

        public D3D11Buffer CreateConstantBuffer(uint byteWidth)
            => CreateBuffer(new BufferDesc { ByteWidth = (byteWidth + 15) & ~15u, Usage = D3D11.UsageDefault, BindFlags = D3D11.BindConstantBuffer });

        public D3D11ComputeShader CreateComputeShader(nint shaderBytecode, nuint bytecodeLength)
        {
            nint shaderPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createComputeShader = (delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)vtable[18];
            D3D11.ThrowIfFailed(createComputeShader(NativePointer, shaderBytecode, bytecodeLength, nint.Zero, &shaderPointer));
            return new D3D11ComputeShader(shaderPointer);
        }

        public D3D11UnorderedAccessView CreateUnorderedAccessView(D3D11Buffer buffer)
        {
            nint viewPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createUav = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)vtable[8];
            D3D11.ThrowIfFailed(createUav(NativePointer, buffer.NativePointer, nint.Zero, &viewPointer));
            return new D3D11UnorderedAccessView(viewPointer);
        }

        private D3D11Buffer CreateBuffer(BufferDesc desc)
        {
            nint bufferPointer = nint.Zero;
            var vtable = *(nint**)NativePointer;
            var createBuffer = (delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)vtable[3];
            D3D11.ThrowIfFailed(createBuffer(NativePointer, &desc, nint.Zero, &bufferPointer));
            return new D3D11Buffer(bufferPointer);
        }
    }

    private sealed unsafe class D3D11DeviceContext(nint nativePointer) : ComPtr(nativePointer)
    {
        public void UpdateBuffer<T>(D3D11Buffer buffer, ReadOnlySpan<T> values) where T : unmanaged
        {
            fixed (T* source = values)
            {
                UpdateSubresource(buffer, (nint)source, (uint)(values.Length * sizeof(T)));
            }
        }

        public void UpdateValue<T>(D3D11Buffer buffer, T value) where T : unmanaged
            => UpdateSubresource(buffer, (nint)(&value), (uint)sizeof(T));

        public void WriteBuffer<T>(D3D11Buffer buffer, ReadOnlySpan<T> values) where T : unmanaged
        {
            var byteWidth = (uint)(values.Length * sizeof(T));
            var mapped = MapWrite(buffer);
            try
            {
                fixed (T* source = values)
                {
                    Buffer.MemoryCopy(source, (void*)mapped.DataPointer, byteWidth, byteWidth);
                }
            }
            finally
            {
                Unmap(buffer);
            }
        }

        public bool TryMapRead(D3D11Buffer buffer, out MappedSubresource mapped)
        {
            MappedSubresource localMapped = default;
            var vtable = *(nint**)NativePointer;
            var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)vtable[14];
            var result = map(NativePointer, buffer.NativePointer, 0, D3D11.MapRead, D3D11.MapFlagDoNotWait, &localMapped);
            if (result == D3D11.DxgiErrorWasStillDrawing)
            {
                mapped = default;
                return false;
            }

            D3D11.ThrowIfFailed(result);
            mapped = localMapped;
            return true;
        }

        private MappedSubresource MapWrite(D3D11Buffer buffer)
        {
            MappedSubresource mapped = default;
            var vtable = *(nint**)NativePointer;
            var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)vtable[14];
            D3D11.ThrowIfFailed(map(NativePointer, buffer.NativePointer, 0, D3D11.MapWrite, 0, &mapped));
            return mapped;
        }

        public void Unmap(D3D11Buffer buffer)
        {
            var vtable = *(nint**)NativePointer;
            var unmap = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)vtable[15];
            unmap(NativePointer, buffer.NativePointer, 0);
        }

        public void SetComputeShader(D3D11ComputeShader shader)
        {
            var vtable = *(nint**)NativePointer;
            var csSetShader = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)vtable[69];
            csSetShader(NativePointer, shader.NativePointer, nint.Zero, 0);
        }

        public void SetComputeConstantBuffer(D3D11Buffer buffer)
        {
            var pointer = buffer.NativePointer;
            var vtable = *(nint**)NativePointer;
            var set = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)vtable[71];
            set(NativePointer, 0, 1, &pointer);
        }

        public void SetComputeUnorderedAccessView(D3D11UnorderedAccessView view)
        {
            var pointer = view.NativePointer;
            uint initialCount = uint.MaxValue;
            var vtable = *(nint**)NativePointer;
            var set = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)vtable[68];
            set(NativePointer, 0, 1, &pointer, &initialCount);
        }

        public void UnsetComputeUnorderedAccessView()
        {
            nint pointer = nint.Zero;
            uint initialCount = uint.MaxValue;
            var vtable = *(nint**)NativePointer;
            var set = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)vtable[68];
            set(NativePointer, 0, 1, &pointer, &initialCount);
        }

        public void Dispatch(uint x)
        {
            var vtable = *(nint**)NativePointer;
            var dispatch = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)vtable[41];
            dispatch(NativePointer, x, 1, 1);
        }

        public void CopyResource(D3D11Buffer destination, D3D11Buffer source)
        {
            var vtable = *(nint**)NativePointer;
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)vtable[47];
            copy(NativePointer, destination.NativePointer, source.NativePointer);
        }

        private void UpdateSubresource(D3D11Buffer buffer, nint source, uint byteWidth)
        {
            var vtable = *(nint**)NativePointer;
            var update = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, uint, uint, void>)vtable[48];
            update(NativePointer, buffer.NativePointer, 0, nint.Zero, source, byteWidth, 0);
        }
    }

    private sealed class D3D11Buffer(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11ComputeShader(nint nativePointer) : ComPtr(nativePointer);
    private sealed class D3D11UnorderedAccessView(nint nativePointer) : ComPtr(nativePointer);

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
    private struct MappedSubresource
    {
        public nint DataPointer;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
