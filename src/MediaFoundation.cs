// Just enough Media Foundation COM interop to drive Windows' built-in H.264 decoder MFT.
// Methods named _N are placeholders that keep vtable slots in order; they are never called.
using System;
using System.Runtime.InteropServices;

namespace PaugeChamp
{
    internal static class MF
    {
        public const int MF_VERSION = 0x00020070;
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
        public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

        public const uint MFT_MESSAGE_COMMAND_FLUSH = 0;
        public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
        public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
        public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

        public static readonly Guid CLSID_MSH264DecoderMFT = new Guid("62CE7E72-4C71-4d20-B15D-452831A87D9D");
        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new Guid("d7388766-18fe-48c6-a177-ee894867c8c4");
        public static readonly Guid MF_MT_YUV_MATRIX = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");
        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid CODECAPI_AVLowLatencyMode = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType type);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

        public static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new COMException(what + " failed", hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;
        public uint dwStatus;
        public IntPtr pEvents;
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        void _0(); void _1(); void _2(); void _3();
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void _6();
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        void _8(); void _9(); void _10(); void _11();
        [PreserveSig] int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, int size, out int written);
        void _13(); void _14(); void _15(); void _16(); void _17();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void _20();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        void _0(); void _1(); void _2(); void _3();
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void _6();
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        void _8(); void _9(); void _10(); void _11();
        [PreserveSig] int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buf, int size, out int written);
        void _13(); void _14(); void _15(); void _16(); void _17();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void _20();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
        // IMFMediaType
        void _30(); void _31(); void _32(); void _33(); void _34();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
        void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
        void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
        // IMFSample
        void _30(); void _31();
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        void _34();
        [PreserveSig] int SetSampleDuration(long duration);
        void _36(); void _37();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        void _40(); void _41(); void _42(); void _43();
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFTransform
    {
        void _0(); void _1(); void _2(); void _3();
        [PreserveSig] int GetOutputStreamInfo(uint streamId, out MFT_OUTPUT_STREAM_INFO info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        void _6(); void _7(); void _8(); void _9(); void _10();
        [PreserveSig] int GetOutputAvailableType(uint streamId, uint index, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint streamId, IMFMediaType type, uint flags);
        [PreserveSig] int SetOutputType(uint streamId, IMFMediaType type, uint flags);
        void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
        [PreserveSig] int ProcessMessage(uint message, IntPtr param);
        [PreserveSig] int ProcessInput(uint streamId, IMFSample sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint count,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] MFT_OUTPUT_DATA_BUFFER[] buffers, out uint status);
    }
}
