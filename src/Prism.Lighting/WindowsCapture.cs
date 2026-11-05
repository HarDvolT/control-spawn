using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Prism.Core;

namespace Prism.Lighting;

/// <summary>Explicitly started WASAPI shared-render-endpoint loopback. Construction does no I/O.
/// Run on a dedicated worker; never instantiated by mock composition.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopback : IAudioLevels
{
    private double[] bands = new double[12];
    private double beat;
    public IReadOnlyList<double> Bands => Volatile.Read(ref bands);
    public double Beat => Volatile.Read(ref beat);
    public void Run(CancellationToken token)
    {
        object? enumerator = null, endpoint = null, clientObject = null, captureObject = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!);
            ((IMMDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 0, out var device); endpoint = device;
            var clientId = typeof(IAudioClient).GUID;
            device.Activate(ref clientId, 23, IntPtr.Zero, out clientObject);
            var client = (IAudioClient)clientObject;
            client.GetMixFormat(out format);
            ushort tag = (ushort)Marshal.ReadInt16(format), channels = (ushort)Marshal.ReadInt16(format, 2), bits = (ushort)Marshal.ReadInt16(format, 14);
            int sampleRate = Marshal.ReadInt32(format, 4), stride = (ushort)Marshal.ReadInt16(format, 12);
            bool floating = tag == 3 || tag == 0xfffe && Marshal.PtrToStructure<Guid>(format + 24) == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (!(floating && bits == 32 || !floating && bits == 16) || channels == 0) throw new NotSupportedException("Unsupported loopback mix format.");
            client.Initialize(0, 0x20000, 1_000_000, 0, format, IntPtr.Zero);
            var captureId = typeof(IAudioCaptureClient).GUID; client.GetService(ref captureId, out captureObject);
            var capture = (IAudioCaptureClient)captureObject;
            client.Start();
            try
            {
                var samples = new List<double>(2048);
                while (!token.IsCancellationRequested)
                {
                    capture.GetNextPacketSize(out uint available);
                    while (available > 0)
                    {
                        capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _);
                        try
                        {
                            for (int f = 0; f < frames; f++)
                            {
                                double value = 0;
                                if ((flags & 2) == 0)
                                    for (int c = 0; c < channels; c++) value += floating
                                        ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, f * stride + c * 4))
                                        : Marshal.ReadInt16(data, f * stride + c * 2) / 32768d;
                                samples.Add(value / channels);
                                if (samples.Count == 1024)
                                {
                                    var next = AudioAnalysis.Spectrum(samples, sampleRate);
                                    var old = Bands; for (int j = 0; j < next.Length; j++) next[j] = old[j] * 0.55 + next[j] * 0.45;
                                    Volatile.Write(ref beat, Math.Clamp(next.Take(3).Average() * 1.5, 0, 1));
                                    Volatile.Write(ref bands, next); samples.Clear();
                                }
                            }
                        }
                        finally { if (frames > 0) capture.ReleaseBuffer(frames); }
                        capture.GetNextPacketSize(out available);
                    }
                    if (available == 0) { Volatile.Write(ref bands, Bands.Select(x => x * 0.93).ToArray()); Volatile.Write(ref beat, Beat * 0.93); }
                    token.WaitHandle.WaitOne(10);
                }
            }
            finally { client.Stop(); }
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            foreach (var value in new[] { captureObject, clientObject, endpoint, enumerator }) if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            Volatile.Write(ref bands, new double[12]); Volatile.Write(ref beat, 0);
        }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice { void Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object value); }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        void GetBufferSize(out uint size); void GetStreamLatency(out long latency); void GetCurrentPadding(out uint padding);
        void IsFormatSupported(int mode, IntPtr format, out IntPtr closest); void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long normal, out long minimum); void Start(); void Stop(); void Reset();
        void SetEventHandle(IntPtr handle); void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong counter);
        void ReleaseBuffer(uint frames); void GetNextPacketSize(out uint frames);
    }
}

public static class AudioAnalysis
{
    // Log-spaced frequency bins, Hann window; pure and fixture-testable.
    public static double[] Spectrum(IReadOnlyList<double> samples, int sampleRate)
    {
        if (sampleRate <= 0 || samples.Count == 0) return new double[12];
        var result = new double[12];
        for (int b = 0; b < result.Length; b++)
        {
            double frequency = 55 * Math.Pow(2, b * 0.72), re = 0, im = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                double s = (double.IsFinite(samples[i]) ? samples[i] : 0) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Math.Max(1, samples.Count - 1)));
                double a = 2 * Math.PI * frequency * i / sampleRate; re += s * Math.Cos(a); im += s * Math.Sin(a);
            }
            result[b] = Math.Clamp(Math.Sqrt(re * re + im * im) / samples.Count * 12, 0, 1);
        }
        return result;
    }
}

/// <summary>DXGI Desktop Duplication, output zero on the default adapter. Explicit Run only.
/// HRESULT access-lost is allowed to escape so the owner can recreate the duplication session.</summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopDuplication : IScreenZones
{
    private RgbColor[] colors = [RgbColor.Black, RgbColor.Black, RgbColor.Black];
    public RgbColor Left => Volatile.Read(ref colors)[0];
    public RgbColor Centre => Volatile.Read(ref colors)[1];
    public RgbColor Right => Volatile.Read(ref colors)[2];
    public void Run(CancellationToken token)
    {
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero, duplication = IntPtr.Zero, staging = IntPtr.Zero;
        var info = Marshal.AllocHGlobal(128);
        try
        {
            Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context));
            var dxgiId = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C"); Check(Marshal.QueryInterface(device, in dxgiId, out dxgi));
            Check(Method<OutPointer>(dxgi, 7)(dxgi, out adapter));
            Check(Method<EnumOutput>(adapter, 7)(adapter, 0, out output));
            var outputId = new Guid("00CDDEA8-939B-4B83-A340-A685226666CC"); Check(Marshal.QueryInterface(output, in outputId, out output1));
            Check(Method<Duplicate>(output1, 22)(output1, device, out duplication));
            while (!token.IsCancellationRequested)
            {
                int hr = Method<Acquire>(duplication, 8)(duplication, 50, info, out var resource);
                if (hr == unchecked((int)0x887A0027)) continue; // wait timeout, no new desktop frame
                Check(hr);
                IntPtr texture = IntPtr.Zero;
                try
                {
                    var textureId = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C"); Check(Marshal.QueryInterface(resource, in textureId, out texture));
                    Method<GetDesc>(texture, 10)(texture, out var desc);
                    if (staging == IntPtr.Zero)
                    {
                        desc.Usage = 3; desc.BindFlags = 0; desc.CpuAccessFlags = 0x20000; desc.MiscFlags = 0;
                        Check(Method<CreateTexture>(device, 5)(device, ref desc, IntPtr.Zero, out staging));
                    }
                    Method<CopyResource>(context, 47)(context, staging, texture);
                    Check(Method<MapResource>(context, 14)(context, staging, 0, 1, 0, out var mapped));
                    try
                    {
                        var totals = new long[9]; var counts = new int[3];
                        for (int y = 0; y < desc.Height; y += 24)
                            for (int x = 0; x < desc.Width; x += 24)
                            {
                                int zone = Math.Min(2, (int)(x * 3 / desc.Width)), offset = y * (int)mapped.RowPitch + x * 4;
                                totals[zone * 3] += Marshal.ReadByte(mapped.Data, offset + 2); totals[zone * 3 + 1] += Marshal.ReadByte(mapped.Data, offset + 1); totals[zone * 3 + 2] += Marshal.ReadByte(mapped.Data, offset); counts[zone]++;
                            }
                        Volatile.Write(ref colors, Enumerable.Range(0, 3).Select(z => new RgbColor((byte)(totals[z * 3] / Math.Max(1, counts[z])), (byte)(totals[z * 3 + 1] / Math.Max(1, counts[z])), (byte)(totals[z * 3 + 2] / Math.Max(1, counts[z])))).ToArray());
                    }
                    finally { Method<UnmapResource>(context, 15)(context, staging, 0); }
                }
                finally { Release(texture); Release(resource); Check(Method<NoArgs>(duplication, 14)(duplication)); }
                token.WaitHandle.WaitOne(33);
            }
        }
        finally
        {
            foreach (var ptr in new[] { staging, duplication, output1, output, adapter, dxgi, context, device }) Release(ptr);
            Marshal.FreeHGlobal(info); Volatile.Write(ref colors, [RgbColor.Black, RgbColor.Black, RgbColor.Black]);
        }
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Release(IntPtr ptr) { if (ptr != IntPtr.Zero) Marshal.Release(ptr); }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPointer(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutput(IntPtr self, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Duplicate(IntPtr self, IntPtr device, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Acquire(IntPtr self, uint timeout, IntPtr info, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDesc(IntPtr self, out TextureDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self, ref TextureDesc desc, IntPtr data, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapResource(IntPtr self, IntPtr resource, uint subresource, uint type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapResource(IntPtr self, IntPtr resource, uint subresource);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out uint featureLevel, out IntPtr context);
}
