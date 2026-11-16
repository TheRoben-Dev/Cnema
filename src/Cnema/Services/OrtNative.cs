using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Cnema.Services;

/// <summary>ONNX Runtime's native library comes with the optional model downloads, not with the app.</summary>
public static class OrtNative
{
    private static readonly object Gate = new();
    private static bool _set;

    /// <summary>Points ONNX Runtime at the downloaded library; once per run, before the first session.</summary>
    public static void Ensure()
    {
        lock (Gate)
        {
            if (_set) return;
            var lib = ModelStore.PathOf(ModelStore.OnnxRuntime);
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly,
                (name, _, _) => name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(lib) : IntPtr.Zero);
            _set = true;
        }
    }

    /// <summary>Background work: some threads, never all.</summary>
    public static SessionOptions Options(int maxThreads = 4) => new()
    {
        IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, maxThreads),
        InterOpNumThreads = 1,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
    };
}
