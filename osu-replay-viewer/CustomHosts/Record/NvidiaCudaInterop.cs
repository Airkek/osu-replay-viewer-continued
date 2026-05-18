using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ManagedCuda;
using ManagedCuda.BasicTypes;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal static unsafe class NvidiaCudaInterop
{
    private const uint cuda_success = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct AVCudaDeviceContext
    {
        public IntPtr CudaContext; // CUcontext cuda_ctx
        public IntPtr Stream;      // CUstream stream
        public IntPtr Internal;    // AVCUDADeviceContextInternal*
    }

    public static CUcontext GetCudaContext(AVBufferRef* hwDeviceRef)
    {
        if (hwDeviceRef == null || hwDeviceRef->data == null)
            throw new InvalidOperationException("FFmpeg CUDA device context is not initialized.");

        var deviceContext = (AVHWDeviceContext*)hwDeviceRef->data;
        if (deviceContext->hwctx == null)
            throw new InvalidOperationException("FFmpeg CUDA hwctx is not initialized.");

        var cudaContext = (AVCudaDeviceContext*)deviceContext->hwctx;
        if (cudaContext->CudaContext == IntPtr.Zero)
            throw new InvalidOperationException("FFmpeg CUDA context is not available.");

        return new CUcontext { Pointer = cudaContext->CudaContext };
    }

    public static CUstream GetCudaStream(AVBufferRef* hwDeviceRef)
    {
        if (hwDeviceRef == null || hwDeviceRef->data == null)
            return new CUstream();

        var deviceContext = (AVHWDeviceContext*)hwDeviceRef->data;
        var cudaContext = (AVCudaDeviceContext*)deviceContext->hwctx;
        return new CUstream { Pointer = cudaContext->Stream };
    }

    public static CudaContextScope PushContext(CUcontext context)
    {
        Check(DriverAPINativeMethods.ContextManagement.cuCtxPushCurrent_v2(context), "cuCtxPushCurrent");
        return new CudaContextScope();
    }

    public static void CopyArrayToDevice2D(CUarray source, int sourceY, IntPtr destination, int destinationPitch, int width, int height)
    {
        var copy = new CUDAMemCpy2D
        {
            srcXInBytes = 0,
            srcY = sourceY,
            srcMemoryType = CUMemoryType.Array,
            srcArray = source,
            dstXInBytes = 0,
            dstY = 0,
            dstMemoryType = CUMemoryType.Device,
            dstDevice = new CUdeviceptr { Pointer = (SizeT)(ulong)destination.ToInt64() },
            dstPitch = destinationPitch,
            WidthInBytes = width,
            Height = height,
        };

        Check(DriverAPINativeMethods.SynchronousMemcpy_v2.cuMemcpy2D_v2(ref copy), "cuMemcpy2D");
    }
    
    public static void CopyArrayToDevice2DAsync(
        CUarray source,
        int sourceY,
        IntPtr destination,
        int destinationPitch,
        int width,
        int height,
        CUstream stream)
    {
        var copy = new CUDAMemCpy2D
        {
            srcXInBytes = 0,
            srcY = sourceY,
            srcMemoryType = CUMemoryType.Array,
            srcArray = source,

            dstXInBytes = 0,
            dstY = 0,
            dstMemoryType = CUMemoryType.Device,
            dstDevice = new CUdeviceptr
            {
                Pointer = (SizeT)unchecked((ulong)destination.ToInt64())
            },
            dstPitch = destinationPitch,

            WidthInBytes = width,
            Height = height,
        };

        Check(
            DriverAPINativeMethods.AsynchronousMemcpy_v2.cuMemcpy2DAsync_v2(ref copy, stream),
            "cuMemcpy2DAsync");
    }

    public static void StreamSynchronize(CUstream stream)
    {
        Check(DriverAPINativeMethods.Streams.cuStreamSynchronize(stream), "cuStreamSynchronize");
    }

    public static void Check(CUResult result, string call)
    {
        if ((uint)result != cuda_success)
            throw new InvalidOperationException($"{call} failed with CUDA error {result}.");
    }

    public sealed class CudaContextScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            CUcontext previous = default;
            Check(DriverAPINativeMethods.ContextManagement.cuCtxPopCurrent_v2(ref previous), "cuCtxPopCurrent");
        }
    }
}
